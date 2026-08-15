using System.Reflection;
using Microsoft.Extensions.Hosting;
using Npgsql;
using MediaBrowser.Controller.Library;

namespace Jellyfin.HotCache.Worker;

/// <summary>
/// Applies the worker-owned, idempotent queue schema before worker polling starts.
/// </summary>
public sealed class HotCacheSchemaMigrationService(NpgsqlDataSource dataSource) : IHostedService
{
    /// <inheritdoc />
    public async Task StartAsync(CancellationToken cancellationToken)
    {
        await PostgreSqlHotCacheSchemaMigrator.ApplyAsync(dataSource, cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}
