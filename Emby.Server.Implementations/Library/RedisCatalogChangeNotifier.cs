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
    private readonly RedisConnectionManager _redis;
    private readonly ILogger<RedisCatalogChangeNotifier> _logger;
    private readonly string _source = Guid.NewGuid().ToString("N");
    private readonly object _publishLock = new();
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
        _redis.ExecuteAsync(connection => SynchronizeAsync(connection, true, false)).GetAwaiter().GetResult();
    }

    /// <inheritdoc />
    public event Action<CatalogChange>? Changed;

    /// <inheritdoc />
    public void Publish(CatalogChange change)
    {
        try
        {
            lock (_publishLock)
            {
                _redis.ExecuteAsync(async connection =>
                    {
                        var sequence = await connection.GetDatabase().StringIncrementAsync(SequenceKey).ConfigureAwait(false);
                        var message = Serialize(change with { Sequence = sequence });
                        await connection.GetSubscriber().PublishAsync(RedisChannel.Literal(ChannelName), message).ConfigureAwait(false);
                        return true;
                    })
                    .GetAwaiter()
                    .GetResult();
            }
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
            _lastSequence = Math.Max(_lastSequence, sequence);
            if (forceFullResync)
            {
                changed = Changed;
            }
        }

        changed?.Invoke(CatalogChange.FullResync(sequence));
        return true;
    }

    private void OnConnectionReplaced(IConnectionMultiplexer connection)
    {
        try
        {
            SynchronizeAsync(connection, true, true).GetAwaiter().GetResult();
        }
        catch (RedisException ex)
        {
            _logger.LogWarning(ex, "Failed to restore catalog invalidation subscription after reconnecting Redis.");
        }
    }

    private void OnConnectionRestored(IConnectionMultiplexer connection)
    {
        try
        {
            // StackExchange.Redis restores subscriptions on the same multiplexer. Compare the
            // durable generation immediately so missed pub/sub messages cannot leave stale caches.
            SynchronizeAsync(connection, false, true).GetAwaiter().GetResult();
        }
        catch (RedisException ex)
        {
            _logger.LogWarning(ex, "Failed to synchronize catalog invalidations after Redis restored its connection.");
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

    private string Serialize(CatalogChange change)
        => string.Create(
            CultureInfo.InvariantCulture,
            $"{_source}|{change.Sequence}|{(int)change.Kind}|{change.ItemId:N}|{change.ParentId:N}");

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
