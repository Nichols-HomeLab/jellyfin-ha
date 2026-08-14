using System.Reflection;
using Microsoft.Extensions.Hosting;
using Npgsql;

namespace Jellyfin.HotCache.Worker;

/// <summary>
/// Applies the worker-owned, idempotent queue schema before worker polling starts.
/// </summary>
public sealed class HotCacheSchemaMigrationService(NpgsqlDataSource dataSource) : IHostedService
{
    /// <inheritdoc />
    public async Task StartAsync(CancellationToken cancellationToken)
    {
        var assembly = typeof(HotCacheSchemaMigrationService).Assembly;
        await using var stream = assembly.GetManifestResourceStream("Jellyfin.HotCache.Worker.sql.001_hot_cache.sql")
            ?? throw new InvalidOperationException("The hot-cache schema migration is missing from the worker image.");
        using var reader = new StreamReader(stream);
        var migration = await reader.ReadToEndAsync(cancellationToken).ConfigureAwait(false);
        await using var command = dataSource.CreateCommand(migration);
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}
