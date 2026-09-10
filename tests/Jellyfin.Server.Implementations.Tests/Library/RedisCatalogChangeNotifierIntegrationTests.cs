#pragma warning disable SER007 // Redis 3 requires an explicit error kind for simulated server faults.

using System;
using System.Collections.Concurrent;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Emby.Server.Implementations.Library;
using Emby.Server.Implementations.MediaEncoding;
using MediaBrowser.Controller.Library;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using StackExchange.Redis;
using Xunit;

namespace Jellyfin.Server.Implementations.Tests.Library;

public sealed class RedisCatalogChangeNotifierIntegrationTests
{
    [Fact]
    [Trait("Category", "IntegrationTest")]
    public async Task ConcurrentPublishers_AllocateOneGloballyOrderedSequenceAndSuppressSelfMessages()
    {
        var connectionString = Environment.GetEnvironmentVariable("JELLYFIN_TEST_REDIS");
        Assert.SkipWhen(string.IsNullOrWhiteSpace(connectionString), "Set JELLYFIN_TEST_REDIS to run Redis integration tests.");
        await ResetSequenceAsync(connectionString).ConfigureAwait(true);

        using var redisA = CreateManager(connectionString);
        using var redisB = CreateManager(connectionString);
        using var redisObserver = CreateManager(connectionString);
        using var notifierA = new RedisCatalogChangeNotifier(redisA, NullLogger<RedisCatalogChangeNotifier>.Instance);
        using var notifierB = new RedisCatalogChangeNotifier(redisB, NullLogger<RedisCatalogChangeNotifier>.Instance);
        using var observer = new RedisCatalogChangeNotifier(redisObserver, NullLogger<RedisCatalogChangeNotifier>.Instance);
        var receivedA = new ConcurrentQueue<CatalogChange>();
        var receivedB = new ConcurrentQueue<CatalogChange>();
        var receivedObserver = new ConcurrentQueue<CatalogChange>();
        notifierA.Changed += receivedA.Enqueue;
        notifierB.Changed += receivedB.Enqueue;
        observer.Changed += receivedObserver.Enqueue;

        await WaitUntilAsync(() => notifierA.IsSynchronized && notifierB.IsSynchronized && observer.IsSynchronized).ConfigureAwait(true);

        // Two active writer streams exercise cross-process ordering without occupying a
        // thread-pool thread for every synchronous publish while Redis waits for completions.
        var publishesA = Task.Run(() =>
        {
            for (var i = 0; i < 50; i++)
            {
                notifierA.Publish(CatalogChange.Local(CatalogChangeKind.Updated, Guid.NewGuid()));
            }
        });
        var publishesB = Task.Run(() =>
        {
            for (var i = 0; i < 50; i++)
            {
                notifierB.Publish(CatalogChange.Local(CatalogChangeKind.Updated, Guid.NewGuid()));
            }
        });
        await Task.WhenAll(publishesA, publishesB).ConfigureAwait(true);
        await WaitUntilAsync(() => receivedObserver.Count == 100).ConfigureAwait(true);
        await WaitUntilAsync(() => receivedA.Count == 50 && receivedB.Count == 50).ConfigureAwait(true);

        Assert.Equal(Enumerable.Range(1, 100).Select(value => (long)value), receivedObserver.Select(change => change.Sequence));
        Assert.Equal(50, receivedA.Count);
        Assert.Equal(50, receivedB.Count);
        Assert.DoesNotContain(receivedObserver, change => change.Kind == CatalogChangeKind.FullResync);
    }

    [Fact]
    [Trait("Category", "IntegrationTest")]
    public async Task ReplicasReceiveOrderedChangesAndRecoverFromGapAndReconnect()
    {
        var connectionString = Environment.GetEnvironmentVariable("JELLYFIN_TEST_REDIS");
        Assert.SkipWhen(string.IsNullOrWhiteSpace(connectionString), "Set JELLYFIN_TEST_REDIS to run Redis integration tests.");

        await ResetSequenceAsync(connectionString).ConfigureAwait(true);

        using var replicaARedis = CreateManager(connectionString);
        var replicaBFactoryCalls = 0;
        using var replicaBRedis = new RedisConnectionManager(
            () =>
            {
                replicaBFactoryCalls++;
                return ConnectionMultiplexer.Connect(connectionString);
            },
            NullLogger<RedisConnectionManager>.Instance);
        var replicaALogger = new RecordingLogger<RedisCatalogChangeNotifier>();
        var replicaBLogger = new RecordingLogger<RedisCatalogChangeNotifier>();
        using var replicaA = new RedisCatalogChangeNotifier(replicaARedis, replicaALogger);
        using var replicaB = new RedisCatalogChangeNotifier(replicaBRedis, replicaBLogger);
        var replicaAChanges = new ConcurrentQueue<CatalogChange>();
        var replicaBChanges = new ConcurrentQueue<CatalogChange>();
        replicaA.Changed += replicaAChanges.Enqueue;
        replicaB.Changed += replicaBChanges.Enqueue;
        await WaitUntilAsync(() => replicaA.IsSynchronized && replicaB.IsSynchronized).ConfigureAwait(true);
        var firstId = Guid.NewGuid();
        var secondId = Guid.NewGuid();

        replicaA.Publish(CatalogChange.Local(CatalogChangeKind.Updated, firstId));
        replicaA.Publish(CatalogChange.Local(CatalogChangeKind.MediaSegments, secondId));
        try
        {
            await WaitUntilAsync(() => replicaBChanges.Count >= 2).ConfigureAwait(true);
        }
        catch (TaskCanceledException)
        {
            Assert.Fail($"Replica B received {replicaBChanges.Count} changes. Logs: {string.Join("; ", replicaBLogger.Messages)}");
        }

        Assert.Empty(replicaAChanges);
        Assert.Equal([firstId, secondId], replicaBChanges.Select(change => change.ItemId).ToArray());
        Assert.True(replicaBChanges.Select(change => change.Sequence).SequenceEqual(
            replicaBChanges.Select(change => change.Sequence).OrderBy(sequence => sequence)));

        await replicaARedis.ExecuteAsync(connection =>
                connection.GetDatabase().StringIncrementAsync("jellyfin:catalog-cache:v1:sequence"))
            .ConfigureAwait(true);
        var afterGapId = Guid.NewGuid();
        replicaA.Publish(CatalogChange.Local(CatalogChangeKind.Updated, afterGapId));
        await WaitUntilAsync(() => replicaBChanges.Any(change => change.Kind == CatalogChangeKind.FullResync)).ConfigureAwait(true);
        Assert.Contains(replicaBChanges, change => change.Kind == CatalogChangeKind.FullResync);
        Assert.Contains(replicaBChanges, change => change.ItemId.Equals(afterGapId));

        var beforeReconnect = replicaBChanges.Count;
        await replicaBRedis.ExecuteAsync<int>(connection =>
            replicaBFactoryCalls == 1
                ? throw new RedisServerException(RedisErrorKind.ReadOnly, CommandFlags.None, "READONLY test endpoint demotion")
                : Task.FromResult(1)).ConfigureAwait(true);
        await WaitUntilAsync(() => replicaBChanges.Count > beforeReconnect).ConfigureAwait(true);
        Assert.Equal(CatalogChangeKind.FullResync, replicaBChanges.Last().Kind);

        await replicaARedis.ExecuteAsync(connection =>
                connection.GetDatabase().KeyDeleteAsync("jellyfin:catalog-cache:v1:sequence"))
            .ConfigureAwait(true);
        var beforeRollbackRecovery = replicaBChanges.Count;
        var rollbackReconnectAttempts = 0;
        await replicaBRedis.ExecuteAsync<int>(_ =>
            ++rollbackReconnectAttempts == 1
                ? throw new RedisServerException(RedisErrorKind.ReadOnly, CommandFlags.None, "READONLY test Redis restart")
                : Task.FromResult(2)).ConfigureAwait(true);
        await WaitUntilAsync(() => replicaBChanges.Count > beforeRollbackRecovery).ConfigureAwait(true);
        Assert.Equal(0, replicaBChanges.Last().Sequence);
        var newEpochId = Guid.NewGuid();
        replicaA.Publish(CatalogChange.Local(CatalogChangeKind.Updated, newEpochId));
        await WaitUntilAsync(() => replicaBChanges.Any(change => change.ItemId.Equals(newEpochId))).ConfigureAwait(true);
        Assert.Contains(replicaBChanges, change => change.Sequence == 1 && change.ItemId.Equals(newEpochId));
    }

    private static RedisConnectionManager CreateManager(string connectionString)
        => new(
            () => ConnectionMultiplexer.Connect(connectionString),
            NullLogger<RedisConnectionManager>.Instance);

    private static async Task ResetSequenceAsync(string connectionString)
    {
        using var setup = await ConnectionMultiplexer.ConnectAsync(connectionString).ConfigureAwait(true);
        await setup.GetDatabase().KeyDeleteAsync("jellyfin:catalog-cache:v1:sequence").ConfigureAwait(true);
    }

    private static async Task WaitUntilAsync(Func<bool> predicate)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        while (!predicate())
        {
            await Task.Delay(25, timeout.Token).ConfigureAwait(true);
        }
    }

    private sealed class RecordingLogger<T> : ILogger<T>
    {
        public ConcurrentQueue<string> Messages { get; } = new();

        public IDisposable? BeginScope<TState>(TState state)
            where TState : notnull
            => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(
            LogLevel logLevel,
            EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter)
            => Messages.Enqueue(formatter(state, exception));
    }
}
