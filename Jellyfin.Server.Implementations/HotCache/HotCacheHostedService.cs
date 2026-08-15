using System;
using System.Threading;
using System.Threading.Tasks;
using MediaBrowser.Controller.Library;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Server.Implementations.HotCache;

/// <summary>Runs migration, reconciliation, and asynchronous resolver observation persistence.</summary>
public sealed class HotCacheHostedService(PostgreSqlHotCacheCoordinator coordinator, ILogger<HotCacheHostedService> logger) : BackgroundService
{
    /// <inheritdoc />
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromMinutes(5));
        do
        {
            try
            {
                await coordinator.EnsureMigratedAsync(stoppingToken).ConfigureAwait(false);
                await coordinator.ReconcileAsync(stoppingToken).ConfigureAwait(false);
                await coordinator.DrainObservationsAsync(stoppingToken).ConfigureAwait(false);
            }
            catch (Exception ex) when (!stoppingToken.IsCancellationRequested)
            {
                logger.LogWarning(ex, "Hot-cache coordination failed; playback continues from canonical storage.");
            }
        }
        while (await timer.WaitForNextTickAsync(stoppingToken).ConfigureAwait(false));
    }
}
