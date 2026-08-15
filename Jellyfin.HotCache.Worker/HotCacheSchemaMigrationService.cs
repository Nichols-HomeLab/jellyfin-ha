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
        await using var command = dataSource.CreateCommand(HotCacheSchema.Sql);
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}
