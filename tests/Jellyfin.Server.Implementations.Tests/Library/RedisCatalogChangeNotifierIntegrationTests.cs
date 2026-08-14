using System;
using System.Collections.Concurrent;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Emby.Server.Implementations.Library;
using Emby.Server.Implementations.MediaEncoding;
using MediaBrowser.Controller.Library;
using Microsoft.Extensions.Logging.Abstractions;
using StackExchange.Redis;
using Xunit;

namespace Jellyfin.Server.Implementations.Tests.Library;

public sealed class RedisCatalogChangeNotifierIntegrationTests
{
    [SkippableFact]
    [Trait("Category", "IntegrationTest")]
    public async Task ReplicasReceiveOrderedChangesAndRecoverFromGapAndReconnect()
    {
        var connectionString = Environment.GetEnvironmentVariable("JELLYFIN_TEST_REDIS");
        Skip.If(string.IsNullOrWhiteSpace(connectionString), "Set JELLYFIN_TEST_REDIS to run Redis integration tests.");

        using (var setup = await ConnectionMultiplexer.ConnectAsync(connectionString).ConfigureAwait(false))
        {
            await setup.GetDatabase().KeyDeleteAsync("jellyfin:catalog-cache:v1:sequence").ConfigureAwait(false);
        }

        using var replicaARedis = CreateManager(connectionString);
        var replicaBFactoryCalls = 0;
        using var replicaBRedis = new RedisConnectionManager(
            () =>
            {
                replicaBFactoryCalls++;
                return ConnectionMultiplexer.Connect(connectionString);
            },
            NullLogger<RedisConnectionManager>.Instance);
        using var replicaA = new RedisCatalogChangeNotifier(replicaARedis, NullLogger<RedisCatalogChangeNotifier>.Instance);
        using var replicaB = new RedisCatalogChangeNotifier(replicaBRedis, NullLogger<RedisCatalogChangeNotifier>.Instance);
        var replicaAChanges = new ConcurrentQueue<CatalogChange>();
        var replicaBChanges = new ConcurrentQueue<CatalogChange>();
        replicaA.Changed += replicaAChanges.Enqueue;
        replicaB.Changed += replicaBChanges.Enqueue;
        var firstId = Guid.NewGuid();
        var secondId = Guid.NewGuid();

        replicaA.Publish(CatalogChange.Local(CatalogChangeKind.Updated, firstId));
        replicaA.Publish(CatalogChange.Local(CatalogChangeKind.MediaSegments, secondId));
        await WaitUntilAsync(() => replicaBChanges.Count == 2).ConfigureAwait(false);

        Assert.Empty(replicaAChanges);
        Assert.Equal([firstId, secondId], replicaBChanges.Select(change => change.ItemId).ToArray());
        Assert.True(replicaBChanges.Select(change => change.Sequence).SequenceEqual(
            replicaBChanges.Select(change => change.Sequence).OrderBy(sequence => sequence)));

        await replicaARedis.ExecuteAsync(connection =>
                connection.GetDatabase().StringIncrementAsync("jellyfin:catalog-cache:v1:sequence"))
            .ConfigureAwait(false);
        var afterGapId = Guid.NewGuid();
        replicaA.Publish(CatalogChange.Local(CatalogChangeKind.Updated, afterGapId));
        await WaitUntilAsync(() => replicaBChanges.Any(change => change.Kind == CatalogChangeKind.FullResync)).ConfigureAwait(false);
        Assert.Contains(replicaBChanges, change => change.Kind == CatalogChangeKind.FullResync);
        Assert.Contains(replicaBChanges, change => change.ItemId == afterGapId);

        var beforeReconnect = replicaBChanges.Count;
        await replicaBRedis.ExecuteAsync<int>(connection =>
            replicaBFactoryCalls == 1
                ? throw new RedisServerException("READONLY test endpoint demotion")
                : Task.FromResult(1)).ConfigureAwait(false);
        await WaitUntilAsync(() => replicaBChanges.Count > beforeReconnect).ConfigureAwait(false);
        Assert.Equal(CatalogChangeKind.FullResync, replicaBChanges.Last().Kind);
    }

    private static RedisConnectionManager CreateManager(string connectionString)
        => new(
            () => ConnectionMultiplexer.Connect(connectionString),
            NullLogger<RedisConnectionManager>.Instance);

    private static async Task WaitUntilAsync(Func<bool> predicate)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        while (!predicate())
        {
            await Task.Delay(25, timeout.Token).ConfigureAwait(false);
        }
    }
}
