using System;
using System.Net;
using System.Threading;
using System.Threading.Tasks;
using Emby.Server.Implementations.Library;
using Emby.Server.Implementations.MediaEncoding;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using StackExchange.Redis;
using Xunit;

namespace Jellyfin.Server.Implementations.Tests.Library;

public sealed class RedisCatalogChangeNotifierTests
{
    [Fact]
    public async Task SubscriptionFailure_DoesNotBlockStartupAndRecoversAsynchronously()
    {
        var connection = new Mock<IConnectionMultiplexer>();
        var subscriber = new Mock<ISubscriber>();
        var database = new Mock<IDatabase>();
        var retryRequested = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var allowRetry = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var synchronized = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var subscriptionAttempts = 0;
        connection.Setup(value => value.GetSubscriber(It.IsAny<object>())).Returns(subscriber.Object);
        connection.Setup(value => value.GetDatabase(It.IsAny<int>(), It.IsAny<object>())).Returns(database.Object);
        database.Setup(value => value.StringGetAsync(It.IsAny<RedisKey>(), CommandFlags.None)).ReturnsAsync(RedisValue.Null);
        subscriber
            .Setup(value => value.SubscribeAsync(It.IsAny<RedisChannel>(), CommandFlags.None))
            .Returns((RedisChannel _, CommandFlags _) =>
            {
                subscriptionAttempts++;
                if (subscriptionAttempts == 1)
                {
                    throw new RedisConnectionException(ConnectionFailureType.UnableToConnect, "test subscription failure");
                }

                synchronized.TrySetResult();
                return Task.FromResult(new Mock<ChannelMessageQueue>().Object);
            });

        using var redis = new RedisConnectionManager(
            () => connection.Object,
            NullLogger<RedisConnectionManager>.Instance);

        RedisCatalogChangeNotifier? notifier = null;
        var exception = Record.Exception(
            () => notifier = new RedisCatalogChangeNotifier(
                redis,
                NullLogger<RedisCatalogChangeNotifier>.Instance,
                cancellationToken =>
                {
                    retryRequested.TrySetResult();
                    return allowRetry.Task.WaitAsync(cancellationToken);
                }));

        Assert.Null(exception);
        using (notifier!)
        {
            await retryRequested.Task.WaitAsync(TimeSpan.FromSeconds(2));
            allowRetry.TrySetResult();
            await synchronized.Task.WaitAsync(TimeSpan.FromSeconds(2));
            Assert.Equal(2, subscriptionAttempts);
        }
    }

    [Fact]
    public void PropagationMetrics_UseTheGitOpsMonitorContract()
    {
        Assert.Equal("jellyfin_catalog_propagation_subscriber_connected", CatalogPropagationMetrics.SubscriberConnectedMetricName);
        Assert.Equal("jellyfin_catalog_propagation_last_applied_sequence", CatalogPropagationMetrics.LastAppliedSequenceMetricName);
        Assert.Equal("jellyfin_catalog_propagation_gap_detections_total", CatalogPropagationMetrics.GapDetectionsMetricName);
        Assert.Equal("jellyfin_catalog_propagation_reconnect_failures_total", CatalogPropagationMetrics.ReconnectFailuresMetricName);
        Assert.Equal("jellyfin_catalog_propagation_full_resync_failures_total", CatalogPropagationMetrics.FullResyncFailuresMetricName);
        Assert.Equal("jellyfin_catalog_propagation_apply_failures_total", CatalogPropagationMetrics.ApplyFailuresMetricName);
    }

    [Fact]
    public void WireMessage_DeserializesCatalogChange()
    {
        var itemId = Guid.NewGuid();
        var message = $"external|1|1|{itemId:N}|00000000000000000000000000000000";

        var parsed = RedisCatalogChangeNotifier.TryDeserialize(message, out var source, out var change);

        Assert.True(parsed);
        Assert.Equal("external", source);
        Assert.Equal(itemId, change.ItemId);
    }

    [Fact]
    public void SameMultiplexerConnectionRestore_RaisesRecoverySignal()
    {
        var connection = new Mock<IConnectionMultiplexer>();
        using var manager = new RedisConnectionManager(
            () => connection.Object,
            NullLogger<RedisConnectionManager>.Instance);
        IConnectionMultiplexer? restored = null;
        manager.ConnectionRestored += value => restored = value;

        connection.Raise(
            c => c.ConnectionRestored += null,
            new ConnectionFailedEventArgs(
                connection.Object,
                new DnsEndPoint("redis", 6379),
                ConnectionType.Interactive,
                ConnectionFailureType.None,
                new InvalidOperationException("test restore"),
                "test restore"));

        Assert.Same(connection.Object, restored);
    }
}
