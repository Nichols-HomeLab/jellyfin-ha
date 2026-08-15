using System;
using System.Globalization;
using System.Threading;
using System.Threading.Tasks;
using Emby.Server.Implementations.MediaEncoding;
using MediaBrowser.Controller.Library;
using Microsoft.Extensions.Logging;
using StackExchange.Redis;

namespace Emby.Server.Implementations.Library;

/// <summary>
/// Distributes ordered catalog cache invalidations over Redis pub/sub.
/// </summary>
public sealed class RedisCatalogChangeNotifier : ICatalogChangeNotifier, IDisposable
{
    private const string ChannelName = "jellyfin:catalog-cache:v1";
    private const string SequenceKey = "jellyfin:catalog-cache:v1:sequence";
    private const string PublishScript = """
        local sequence = redis.call('INCR', KEYS[1])
        local message = ARGV[2] .. '|' .. sequence .. '|' .. ARGV[3] .. '|' .. ARGV[4] .. '|' .. ARGV[5]
        redis.call('PUBLISH', ARGV[1], message)
        return sequence
        """;

    private static readonly TimeSpan SubscriptionRetryDelay = TimeSpan.FromSeconds(5);

    private readonly RedisConnectionManager _redis;
    private readonly ILogger<RedisCatalogChangeNotifier> _logger;
    private readonly Func<CancellationToken, Task> _subscriptionRetryDelay;
    private readonly CancellationTokenSource _subscriptionCancellationTokenSource = new();
    private readonly string _source = Guid.NewGuid().ToString("N");
    private readonly object _receiveLock = new();
    private ChannelMessageQueue? _subscription;
    private long _lastSequence;
    private int _subscriptionWorkerRunning;
    private int _subscriptionFailureLogged;
    private int _subscriptionRequested;
    private int _fullResyncRequested;
    private bool _disposed;

    /// <summary>
    /// Initializes a new instance of the <see cref="RedisCatalogChangeNotifier"/> class.
    /// </summary>
    /// <param name="redis">The shared Redis connection.</param>
    /// <param name="logger">The logger.</param>
    public RedisCatalogChangeNotifier(
        RedisConnectionManager redis,
        ILogger<RedisCatalogChangeNotifier> logger)
        : this(
            redis,
            logger,
            cancellationToken => Task.Delay(SubscriptionRetryDelay, cancellationToken))
    {
    }

    internal RedisCatalogChangeNotifier(
        RedisConnectionManager redis,
        ILogger<RedisCatalogChangeNotifier> logger,
        Func<CancellationToken, Task> subscriptionRetryDelay)
    {
        ArgumentNullException.ThrowIfNull(subscriptionRetryDelay);
        _redis = redis;
        _logger = logger;
        _subscriptionRetryDelay = subscriptionRetryDelay;
        _redis.ConnectionReplaced += OnConnectionReplaced;
        _redis.ConnectionRestored += OnConnectionRestored;
        ScheduleSynchronization(subscribe: true, forceFullResync: false);
    }

    /// <inheritdoc />
    public event Action<CatalogChange>? Changed;

    /// <inheritdoc />
    public void Publish(CatalogChange change)
    {
        try
        {
            // Lua makes sequence allocation and publication one globally ordered operation across
            // every catalog-writer process, rather than only serializing callers in this instance.
            _redis.ExecuteAsync(connection => connection.GetDatabase().ScriptEvaluateAsync(
                    PublishScript,
                    [(RedisKey)SequenceKey],
                    [
                        ChannelName,
                        _source,
                        ((int)change.Kind).ToString(CultureInfo.InvariantCulture),
                        change.ItemId.ToString("N"),
                        change.ParentId.ToString("N")
                    ]))
                .GetAwaiter()
                .GetResult();
        }
        catch (RedisException ex)
        {
            _logger.LogWarning(ex, "Failed to publish catalog cache invalidation; peers will fully resynchronize after Redis reconnect.");
        }
    }

    /// <inheritdoc />
    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _redis.ConnectionReplaced -= OnConnectionReplaced;
        _redis.ConnectionRestored -= OnConnectionRestored;
        _subscriptionCancellationTokenSource.Cancel();
        _subscriptionCancellationTokenSource.Dispose();
        try
        {
            _subscription?.Unsubscribe();
        }
        catch (RedisException)
        {
            // The managed connection may already have been replaced or disposed.
        }

        GC.SuppressFinalize(this);
    }

    private async Task<bool> SynchronizeAsync(
        IConnectionMultiplexer connection,
        bool subscribe,
        bool forceFullResync)
    {
        if (subscribe)
        {
            var subscription = await connection.GetSubscriber()
                .SubscribeAsync(RedisChannel.Literal(ChannelName))
                .ConfigureAwait(false);
            subscription.OnMessage(message => HandleMessage(message.Message));
            _subscription = subscription;
        }

        var value = await connection.GetDatabase().StringGetAsync(SequenceKey).ConfigureAwait(false);
        var sequence = long.TryParse(value.ToString(), NumberStyles.None, CultureInfo.InvariantCulture, out var parsed)
            ? parsed
            : 0;
        Action<CatalogChange>? changed = null;
        lock (_receiveLock)
        {
            if (forceFullResync)
            {
                // A restored Redis instance may have lost its volatile sequence key. Reset the
                // local watermark after invalidating caches so the new low sequence epoch applies.
                _lastSequence = sequence;
                CatalogPropagationMetrics.SetLastAppliedSequence(sequence);
                changed = Changed;
            }
            else
            {
                _lastSequence = Math.Max(_lastSequence, sequence);
                CatalogPropagationMetrics.SetLastAppliedSequence(_lastSequence);
            }
        }

        Dispatch(changed, CatalogChange.FullResync(sequence), true);
        return true;
    }

    private void OnConnectionReplaced(IConnectionMultiplexer connection)
    {
        CatalogPropagationMetrics.SetSubscriberConnected(false);
        ScheduleSynchronization(subscribe: true, forceFullResync: true);
    }

    private void OnConnectionRestored(IConnectionMultiplexer connection)
    {
        CatalogPropagationMetrics.SetSubscriberConnected(false);
        ScheduleSynchronization(subscribe: false, forceFullResync: true);
    }

    private void ScheduleSynchronization(bool subscribe, bool forceFullResync)
    {
        if (_disposed)
        {
            return;
        }

        if (subscribe)
        {
            Interlocked.Exchange(ref _subscriptionRequested, 1);
        }

        if (forceFullResync)
        {
            Interlocked.Exchange(ref _fullResyncRequested, 1);
        }

        if (Interlocked.Exchange(ref _subscriptionWorkerRunning, 1) == 0)
        {
            _ = SynchronizeWithRetryAsync();
        }
    }

    private async Task SynchronizeWithRetryAsync()
    {
        var cancellationToken = _subscriptionCancellationTokenSource.Token;
        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                var subscribe = Interlocked.Exchange(ref _subscriptionRequested, 0) != 0;
                var forceFullResync = Interlocked.Exchange(ref _fullResyncRequested, 0) != 0;
                try
                {
                    // StackExchange.Redis restores subscriptions on the same multiplexer. Compare the
                    // durable generation after reconnect so missed pub/sub messages cannot leave stale caches.
                    await _redis.ExecuteAsync(
                        connection => SynchronizeAsync(connection, subscribe, forceFullResync),
                        cancellationToken).ConfigureAwait(false);
                    Interlocked.Exchange(ref _subscriptionFailureLogged, 0);
                    CatalogPropagationMetrics.SetSubscriberConnected(true);

                    if (Volatile.Read(ref _subscriptionRequested) == 0 && Volatile.Read(ref _fullResyncRequested) == 0)
                    {
                        return;
                    }

                    continue;
                }
                catch (RedisException ex)
                {
                    CatalogPropagationMetrics.RecordReconnectFailure();
                    if (Interlocked.Exchange(ref _subscriptionFailureLogged, 1) == 0)
                    {
                        _logger.LogWarning(ex, "Failed to synchronize Redis catalog invalidations; retrying asynchronously.");
                    }
                }
                catch (ObjectDisposedException)
                {
                    return;
                }

                if (subscribe)
                {
                    Interlocked.Exchange(ref _subscriptionRequested, 1);
                }

                if (forceFullResync)
                {
                    Interlocked.Exchange(ref _fullResyncRequested, 1);
                }

                try
                {
                    await _subscriptionRetryDelay(cancellationToken).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    return;
                }
            }
        }
        finally
        {
            Volatile.Write(ref _subscriptionWorkerRunning, 0);
        }
    }

    private void HandleMessage(RedisValue value)
    {
        _logger.LogDebug("Received Redis catalog invalidation message.");
        if (!TryDeserialize(value.ToString(), out var source, out var change))
        {
            CatalogPropagationMetrics.RecordApplyFailure();
            _logger.LogWarning("Ignored malformed catalog invalidation message.");
            return;
        }

        Action<CatalogChange>? changed;
        var requiresFullResync = false;
        lock (_receiveLock)
        {
            if (change.Sequence <= _lastSequence)
            {
                return;
            }

            requiresFullResync = _lastSequence > 0 && change.Sequence != _lastSequence + 1;
            _lastSequence = change.Sequence;
            CatalogPropagationMetrics.SetLastAppliedSequence(change.Sequence);
            if (source.AsSpan().SequenceEqual(_source.AsSpan()))
            {
                return;
            }

            changed = Changed;
        }

        if (requiresFullResync)
        {
            CatalogPropagationMetrics.RecordGapDetection();
            Dispatch(changed, CatalogChange.FullResync(change.Sequence - 1), true);
        }

        Dispatch(changed, change, false);
    }

    private void Dispatch(Action<CatalogChange>? changed, CatalogChange change, bool fullResync)
    {
        try
        {
            changed?.Invoke(change);
        }
        catch (Exception ex)
        {
            if (fullResync)
            {
                CatalogPropagationMetrics.RecordFullResyncFailure();
            }
            else
            {
                CatalogPropagationMetrics.RecordApplyFailure();
            }

            _logger.LogError(ex, "Failed to apply Redis catalog propagation change at sequence {Sequence}.", change.Sequence);
        }
    }

    internal static bool TryDeserialize(string message, out string source, out CatalogChange change)
    {
        source = string.Empty;
        change = default;
        var parts = message.Split('|');
        if (parts.Length != 5
            || string.IsNullOrEmpty(parts[0])
            || !long.TryParse(parts[1], NumberStyles.None, CultureInfo.InvariantCulture, out var sequence)
            || sequence <= 0
            || !int.TryParse(parts[2], NumberStyles.None, CultureInfo.InvariantCulture, out var kindValue)
            || !Enum.IsDefined(typeof(CatalogChangeKind), kindValue)
            || (CatalogChangeKind)kindValue == CatalogChangeKind.FullResync
            || !Guid.TryParseExact(parts[3], "N", out var itemId)
            || !Guid.TryParseExact(parts[4], "N", out var parentId))
        {
            return false;
        }

        source = parts[0];
        change = new CatalogChange(sequence, (CatalogChangeKind)kindValue, itemId, parentId);
        return true;
    }
}
