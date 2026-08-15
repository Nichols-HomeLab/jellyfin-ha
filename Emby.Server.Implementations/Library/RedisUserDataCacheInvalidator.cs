using System;
using System.Threading;
using System.Threading.Tasks;
using Emby.Server.Implementations.MediaEncoding;
using MediaBrowser.Controller.Library;
using Microsoft.Extensions.Logging;
using StackExchange.Redis;

namespace Emby.Server.Implementations.Library;

/// <summary>
/// Distributes user-data cache invalidations over Redis pub/sub.
/// </summary>
public sealed class RedisUserDataCacheInvalidator : IUserDataCacheInvalidator, IDisposable
{
    private const string ChannelName = "jellyfin:user-data-cache:v1";
    private static readonly TimeSpan SubscriptionRetryDelay = TimeSpan.FromSeconds(5);
    private readonly RedisConnectionManager _redis;
    private readonly ILogger<RedisUserDataCacheInvalidator> _logger;
    private readonly Func<CancellationToken, Task> _subscriptionRetryDelay;
    private readonly CancellationTokenSource _subscriptionCancellationTokenSource = new();
    private readonly string _source = Guid.NewGuid().ToString("N");
    private int _subscriptionWorkerRunning;
    private int _subscriptionFailureLogged;
    private bool _disposed;

    /// <summary>
    /// Initializes a new instance of the <see cref="RedisUserDataCacheInvalidator"/> class.
    /// </summary>
    /// <param name="redis">The shared Redis connection.</param>
    /// <param name="logger">The logger.</param>
    public RedisUserDataCacheInvalidator(
        RedisConnectionManager redis,
        ILogger<RedisUserDataCacheInvalidator> logger)
        : this(
            redis,
            logger,
            cancellationToken => Task.Delay(SubscriptionRetryDelay, cancellationToken))
    {
    }

    internal RedisUserDataCacheInvalidator(
        RedisConnectionManager redis,
        ILogger<RedisUserDataCacheInvalidator> logger,
        Func<CancellationToken, Task> subscriptionRetryDelay)
    {
        ArgumentNullException.ThrowIfNull(subscriptionRetryDelay);
        _redis = redis;
        _logger = logger;
        _subscriptionRetryDelay = subscriptionRetryDelay;
        _redis.ConnectionReplaced += OnConnectionReplaced;
        ScheduleSubscription();
    }

    /// <inheritdoc />
    public event Action<string>? Invalidated;

    /// <inheritdoc />
    public void Publish(string cacheKey)
    {
        try
        {
            var message = _source + '|' + cacheKey;
            _redis.ExecuteAsync(connection => connection.GetSubscriber().PublishAsync(
                    RedisChannel.Literal(ChannelName),
                    message))
                .GetAwaiter()
                .GetResult();
        }
        catch (RedisException ex)
        {
            _logger.LogWarning(ex, "Failed to publish a user-data cache invalidation.");
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
        _subscriptionCancellationTokenSource.Cancel();
        _subscriptionCancellationTokenSource.Dispose();
        GC.SuppressFinalize(this);
    }

    private async Task<bool> SubscribeAsync(IConnectionMultiplexer connection)
    {
        await connection.GetSubscriber().SubscribeAsync(
                RedisChannel.Literal(ChannelName),
                (_, value) => HandleMessage(value))
            .ConfigureAwait(false);
        return true;
    }

    private void OnConnectionReplaced(IConnectionMultiplexer connection)
    {
        ScheduleSubscription();
    }

    private void ScheduleSubscription()
    {
        if (_disposed || Interlocked.Exchange(ref _subscriptionWorkerRunning, 1) != 0)
        {
            return;
        }

        _ = SubscribeWithRetryAsync();
    }

    private async Task SubscribeWithRetryAsync()
    {
        var cancellationToken = _subscriptionCancellationTokenSource.Token;
        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                try
                {
                    await _redis.ExecuteAsync(SubscribeAsync, cancellationToken).ConfigureAwait(false);
                    Interlocked.Exchange(ref _subscriptionFailureLogged, 0);
                    return;
                }
                catch (RedisException ex)
                {
                    if (Interlocked.Exchange(ref _subscriptionFailureLogged, 1) == 0)
                    {
                        _logger.LogWarning(
                            ex,
                            "Failed to subscribe to Redis user-data invalidations; retrying asynchronously.");
                    }
                }
                catch (ObjectDisposedException)
                {
                    return;
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
        var message = value.ToString();
        var separator = message.IndexOf('|', StringComparison.Ordinal);
        if (separator <= 0 || message.AsSpan(0, separator).SequenceEqual(_source.AsSpan()))
        {
            return;
        }

        var cacheKey = message[(separator + 1)..];
        if (!string.IsNullOrEmpty(cacheKey))
        {
            Invalidated?.Invoke(cacheKey);
        }
    }
}
