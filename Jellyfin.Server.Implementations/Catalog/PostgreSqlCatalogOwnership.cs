using System;
using System.Data;
using System.Threading;
using System.Threading.Tasks;
using MediaBrowser.Controller.Library;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Npgsql;

namespace Jellyfin.Server.Implementations.Catalog;

/// <summary>
/// Coordinates catalog-writer ownership with a PostgreSQL session advisory lock.
/// </summary>
public sealed class PostgreSqlCatalogOwnership : BackgroundService, ICatalogOwnership, IAsyncDisposable
{
    private const int AdvisoryLockNamespace = 1246573006;
    private const int AdvisoryLockId = 607;
    private readonly NpgsqlDataSource _dataSource;
    private readonly string _instanceId;
    private readonly TimeSpan _probeInterval;
    private readonly ILogger<PostgreSqlCatalogOwnership> _logger;
    private readonly object _stateLock = new();
    private CancellationTokenSource _ownershipLostSource;
    private bool _isOwner;
    private bool _disposed;

    /// <summary>
    /// Initializes a new instance of the <see cref="PostgreSqlCatalogOwnership"/> class.
    /// </summary>
    /// <param name="dataSource">The shared PostgreSQL data source.</param>
    /// <param name="instanceId">The observable server instance identifier.</param>
    /// <param name="probeInterval">The interval between acquisition and owner-health probes.</param>
    /// <param name="logger">The logger.</param>
    public PostgreSqlCatalogOwnership(
        NpgsqlDataSource dataSource,
        string instanceId,
        TimeSpan probeInterval,
        ILogger<PostgreSqlCatalogOwnership> logger)
    {
        ArgumentNullException.ThrowIfNull(dataSource);
        ArgumentException.ThrowIfNullOrWhiteSpace(instanceId);
        ArgumentOutOfRangeException.ThrowIfLessThan(probeInterval, TimeSpan.FromMilliseconds(50));
        ArgumentOutOfRangeException.ThrowIfGreaterThan(probeInterval, TimeSpan.FromSeconds(30));
        ArgumentNullException.ThrowIfNull(logger);

        _dataSource = dataSource;
        _instanceId = instanceId;
        _probeInterval = probeInterval;
        _logger = logger;
        _ownershipLostSource = new CancellationTokenSource();
        _ownershipLostSource.Cancel();
    }

    /// <inheritdoc />
    public bool TryGetCatalogWriteToken(out CancellationToken ownershipLost)
    {
        lock (_stateLock)
        {
            if (_disposed)
            {
                ownershipLost = new CancellationToken(canceled: true);
                return false;
            }

            ownershipLost = _ownershipLostSource.Token;
            return _isOwner && !ownershipLost.IsCancellationRequested;
        }
    }

    /// <inheritdoc />
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        NpgsqlConnection? ownerConnection = null;

        try
        {
            while (!stoppingToken.IsCancellationRequested)
            {
                if (ownerConnection is null)
                {
                    ownerConnection = await TryAcquireAsync(stoppingToken).ConfigureAwait(false);
                    if (ownerConnection is not null)
                    {
                        BecomeOwner();
                    }
                }
                else if (!await IsConnectionHealthyAsync(ownerConnection, stoppingToken).ConfigureAwait(false))
                {
                    LoseOwnership("PostgreSQL coordination became unavailable");
                    await ownerConnection.DisposeAsync().ConfigureAwait(false);
                    ownerConnection = null;
                }

                await Task.Delay(_probeInterval, stoppingToken).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            // Normal hosted-service shutdown.
        }
        finally
        {
            LoseOwnership("server is stopping");
            if (ownerConnection is not null)
            {
                await ReleaseAsync(ownerConnection).ConfigureAwait(false);
                await ownerConnection.DisposeAsync().ConfigureAwait(false);
            }
        }
    }

    /// <inheritdoc />
    public async ValueTask DisposeAsync()
    {
        await StopAsync(CancellationToken.None).ConfigureAwait(false);
        lock (_stateLock)
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            _ownershipLostSource.Dispose();
        }

        Dispose();
    }

    private async Task<NpgsqlConnection?> TryAcquireAsync(CancellationToken cancellationToken)
    {
        NpgsqlConnection? connection = null;
        try
        {
            connection = await _dataSource.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
            await using var command = connection.CreateCommand();
            command.CommandText = "SELECT pg_try_advisory_lock($1, $2)";
            command.Parameters.AddWithValue(AdvisoryLockNamespace);
            command.Parameters.AddWithValue(AdvisoryLockId);
            command.CommandTimeout = GetCommandTimeoutSeconds();

            if (await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false) is true)
            {
                return connection;
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Catalog ownership unavailable for instance {InstanceId}; remaining read-only", _instanceId);
        }

        if (connection is not null)
        {
            await connection.DisposeAsync().ConfigureAwait(false);
        }

        return null;
    }

    private async Task<bool> IsConnectionHealthyAsync(NpgsqlConnection connection, CancellationToken cancellationToken)
    {
        try
        {
            if (connection.State != ConnectionState.Open)
            {
                return false;
            }

            await using var command = connection.CreateCommand();
            command.CommandText = "SELECT 1";
            command.CommandTimeout = GetCommandTimeoutSeconds();
            await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
            return true;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Catalog owner health probe failed for instance {InstanceId}", _instanceId);
            return false;
        }
    }

    private async Task ReleaseAsync(NpgsqlConnection connection)
    {
        try
        {
            await using var command = connection.CreateCommand();
            command.CommandText = "SELECT pg_advisory_unlock($1, $2)";
            command.Parameters.AddWithValue(AdvisoryLockNamespace);
            command.Parameters.AddWithValue(AdvisoryLockId);
            command.CommandTimeout = GetCommandTimeoutSeconds();
            await command.ExecuteScalarAsync(CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Explicit catalog ownership release failed for instance {InstanceId}; closing the PostgreSQL session", _instanceId);
        }
    }

    private int GetCommandTimeoutSeconds()
        => Math.Clamp((int)Math.Ceiling(_probeInterval.TotalSeconds), 1, 30);

    private void BecomeOwner()
    {
        lock (_stateLock)
        {
            if (_isOwner)
            {
                return;
            }

            _ownershipLostSource.Dispose();
            _ownershipLostSource = new CancellationTokenSource();
            _isOwner = true;
        }

        _logger.LogInformation("Instance {InstanceId} acquired cluster-wide catalog-writer ownership", _instanceId);
    }

    private void LoseOwnership(string reason)
    {
        CancellationTokenSource? ownershipLostSource = null;
        lock (_stateLock)
        {
            if (!_isOwner)
            {
                return;
            }

            _isOwner = false;
            ownershipLostSource = _ownershipLostSource;
        }

        ownershipLostSource.Cancel();
        _logger.LogWarning("Instance {InstanceId} lost cluster-wide catalog-writer ownership because {Reason}", _instanceId, reason);
    }
}
