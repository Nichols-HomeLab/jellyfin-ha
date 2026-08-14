using System;
using System.Globalization;
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
    private readonly RedisConnectionManager _redis;
    private readonly ILogger<RedisCatalogChangeNotifier> _logger;
    private readonly string _source = Guid.NewGuid().ToString("N");
    private readonly object _receiveLock = new();
    private ChannelMessageQueue? _subscription;
    private long _lastSequence;
    private bool _disposed;

    /// <summary>
    /// Initializes a new instance of the <see cref="RedisCatalogChangeNotifier"/> class.
    /// </summary>
    /// <param name="redis">The shared Redis connection.</param>
    /// <param name="logger">The logger.</param>
    public RedisCatalogChangeNotifier(
        RedisConnectionManager redis,
        ILogger<RedisCatalogChangeNotifier> logger)
    {
        _redis = redis;
        _logger = logger;
        _redis.ConnectionReplaced += OnConnectionReplaced;
        _redis.ConnectionRestored += OnConnectionRestored;
        // Construction must not return before the subscription is acknowledged; otherwise the
        // first committed catalog change can be missed before this singleton is ready.
        _redis.ExecuteAsync(connection => SynchronizeAsync(connection, true, false)).GetAwaiter().GetResult();
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
                changed = Changed;
            }
            else
            {
                _lastSequence = Math.Max(_lastSequence, sequence);
            }
        }

        changed?.Invoke(CatalogChange.FullResync(sequence));
        return true;
    }

    private void OnConnectionReplaced(IConnectionMultiplexer connection)
        => Recover(connection, true, "restore the catalog invalidation subscription after replacing Redis");

    private void OnConnectionRestored(IConnectionMultiplexer connection)
        => Recover(connection, false, "synchronize catalog invalidations after Redis restored its connection");

    private void Recover(IConnectionMultiplexer connection, bool subscribe, string operation)
    {
        try
        {
            // StackExchange.Redis restores subscriptions on the same multiplexer. Compare the
            // durable generation immediately so missed pub/sub messages cannot leave stale caches.
            // Recovery callbacks are synchronous by contract; completing synchronization here
            // fences later notifications behind the full-resync signal.
            SynchronizeAsync(connection, subscribe, true).GetAwaiter().GetResult();
        }
        catch (RedisException ex)
        {
            _logger.LogWarning(ex, "Failed to {CatalogRecoveryOperation}.", operation);
        }
    }

    private void HandleMessage(RedisValue value)
    {
        _logger.LogDebug("Received Redis catalog invalidation message.");
        if (!TryDeserialize(value.ToString(), out var source, out var change))
        {
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
            if (source.AsSpan().SequenceEqual(_source.AsSpan()))
            {
                return;
            }

            changed = Changed;
        }

        if (requiresFullResync)
        {
            changed?.Invoke(CatalogChange.FullResync(change.Sequence - 1));
        }

        changed?.Invoke(change);
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
