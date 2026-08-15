using System.Threading;
using System.Threading.Tasks;
using MediaBrowser.Controller.Library;
using Npgsql;

namespace MediaBrowser.Controller.Library;

/// <summary>Applies hot-cache migrations once, atomically, under a PostgreSQL advisory lock.</summary>
public static class PostgreSqlHotCacheSchemaMigrator
{
    /// <summary>Brings the hot-cache schema to the latest version.</summary>
    /// <param name="dataSource">The PostgreSQL data source.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>A task that completes after every pending migration commits.</returns>
    public static async Task ApplyAsync(NpgsqlDataSource dataSource, CancellationToken cancellationToken)
    {
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        await ExecuteAsync(connection, transaction, HotCacheSchema.CreateLedgerSql, cancellationToken).ConfigureAwait(false);
        await ExecuteAsync(connection, transaction, "SELECT pg_advisory_xact_lock(8430170)", cancellationToken).ConfigureAwait(false);

        foreach (var migration in HotCacheSchema.Migrations)
        {
            await using var applied = new NpgsqlCommand("SELECT 1 FROM hot_cache_schema_migrations WHERE version=@version", connection, transaction);
            applied.Parameters.AddWithValue("version", migration.Version);
            if (await applied.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false) is not null)
            {
                continue;
            }

            await ExecuteAsync(connection, transaction, migration.Sql, cancellationToken).ConfigureAwait(false);
            await using var record = new NpgsqlCommand("INSERT INTO hot_cache_schema_migrations(version) VALUES (@version)", connection, transaction);
            record.Parameters.AddWithValue("version", migration.Version);
            await record.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }

        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
    }

    private static async Task ExecuteAsync(NpgsqlConnection connection, NpgsqlTransaction transaction, string sql, CancellationToken cancellationToken)
    {
        await using var command = new NpgsqlCommand(sql, connection, transaction);
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }
}
