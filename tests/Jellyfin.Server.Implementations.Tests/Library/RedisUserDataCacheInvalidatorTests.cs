using System;
using System.Threading.Tasks;
using Emby.Server.Implementations.Library;
using Emby.Server.Implementations.MediaEncoding;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using StackExchange.Redis;
using Xunit;

namespace Jellyfin.Server.Implementations.Tests.Library;

public sealed class RedisUserDataCacheInvalidatorTests
{
    [Fact]
    public void Constructor_SubscriptionFailure_DoesNotBlockServiceStartup()
    {
        var connection = new Mock<IConnectionMultiplexer>();
        var subscriber = new Mock<ISubscriber>();
        connection.Setup(value => value.GetSubscriber(It.IsAny<object>())).Returns(subscriber.Object);
        subscriber
            .Setup(value => value.SubscribeAsync(
                It.IsAny<RedisChannel>(),
                It.IsAny<Action<RedisChannel, RedisValue>>(),
                CommandFlags.None))
            .Throws(new RedisConnectionException(ConnectionFailureType.UnableToConnect, "test subscription failure"));

        using var redis = new RedisConnectionManager(
            () => connection.Object,
            NullLogger<RedisConnectionManager>.Instance);

        RedisUserDataCacheInvalidator? invalidator = null;
        var exception = Record.Exception(
            () => invalidator = new RedisUserDataCacheInvalidator(
                redis,
                NullLogger<RedisUserDataCacheInvalidator>.Instance));

        Assert.Null(exception);
        invalidator!.Dispose();
    }

    [Fact]
    public async Task SubscriptionFailure_RetriesAndRestoresInvalidationSubscription()
    {
        var connection = new Mock<IConnectionMultiplexer>();
        var subscriber = new Mock<ISubscriber>();
        var retryRequested = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var allowRetry = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var subscribed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var invalidated = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        Action<RedisChannel, RedisValue>? handler = null;
        var subscriptionAttempts = 0;
        connection.Setup(value => value.GetSubscriber(It.IsAny<object>())).Returns(subscriber.Object);
        subscriber
            .Setup(value => value.SubscribeAsync(
                It.IsAny<RedisChannel>(),
                It.IsAny<Action<RedisChannel, RedisValue>>(),
                CommandFlags.None))
            .Returns((RedisChannel _, Action<RedisChannel, RedisValue> subscribedHandler, CommandFlags _) =>
            {
                subscriptionAttempts++;
                if (subscriptionAttempts == 1)
                {
                    throw new RedisConnectionException(ConnectionFailureType.UnableToConnect, "test subscription failure");
                }

                handler = subscribedHandler;
                subscribed.TrySetResult();
                return Task.CompletedTask;
            });

        using var redis = new RedisConnectionManager(
            () => connection.Object,
            NullLogger<RedisConnectionManager>.Instance);
        using var invalidator = new RedisUserDataCacheInvalidator(
            redis,
            NullLogger<RedisUserDataCacheInvalidator>.Instance,
            cancellationToken =>
            {
                retryRequested.TrySetResult();
                return allowRetry.Task.WaitAsync(cancellationToken);
            });
        invalidator.Invalidated += cacheKey => invalidated.TrySetResult(cacheKey);

        await retryRequested.Task.WaitAsync(TimeSpan.FromSeconds(2));
        allowRetry.TrySetResult();
        await subscribed.Task.WaitAsync(TimeSpan.FromSeconds(2));
        handler!(RedisChannel.Literal("jellyfin:user-data-cache:v1"), "remote|recovered-cache-key");

        Assert.Equal("recovered-cache-key", await invalidated.Task.WaitAsync(TimeSpan.FromSeconds(2)));
        Assert.Equal(2, subscriptionAttempts);
    }
}
