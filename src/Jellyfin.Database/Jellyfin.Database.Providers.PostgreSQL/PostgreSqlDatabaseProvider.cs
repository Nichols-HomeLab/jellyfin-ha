using System;
using System.Collections.Generic;
using System.Data;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Database.Implementations;
using Jellyfin.Database.Implementations.DbConfiguration;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace Jellyfin.Database.Providers.PostgreSQL;

/// <summary>
/// Configures Jellyfin to use a PostgreSQL database.
/// </summary>
[JellyfinDatabaseProviderKey("Jellyfin-PostgreSQL")]
public sealed class PostgreSqlDatabaseProvider : IJellyfinDatabaseProvider
{
    // Sentinel returned by MigrationBackupFast to signal that no file backup was
    // created (PostgreSQL backups are handled externally by jellyfin-pg-backup CronJob).
    private const string NoAutomatedBackupKey = "postgresql-no-automated-backup";

    private readonly NpgsqlDataSource _dataSource;

    /// <summary>
    /// Initializes a new instance of the <see cref="PostgreSqlDatabaseProvider"/> class.
    /// </summary>
    /// <param name="dataSource">The <see cref="NpgsqlDataSource"/> used for PostgreSQL connections.</param>
    public PostgreSqlDatabaseProvider(NpgsqlDataSource dataSource)
    {
        _dataSource = dataSource;
    }

    /// <inheritdoc/>
    public IDbContextFactory<JellyfinDbContext>? DbContextFactory { get; set; }

    /// <summary>
    /// Backfills existing usernames with the same invariant normalization used by Jellyfin,
    /// before the schema migration creates their unique index.
    /// </summary>
    /// <param name="context">The context to upgrade.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>The asynchronous operation.</returns>
    public static async Task PrepareSchemaUpgradeAsync(JellyfinDbContext context, CancellationToken cancellationToken = default)
    {
        var connection = context.Database.GetDbConnection();
        var openedHere = connection.State != ConnectionState.Open;
        if (openedHere)
        {
            await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
        }

        try
        {
            await using var exists = connection.CreateCommand();
            exists.CommandText = "SELECT to_regclass('\"Users\"') IS NOT NULL";
            if (!(bool)(await exists.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false))!)
            {
                return;
            }

            await using var transaction = await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
            await using var command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = "SELECT \"Id\", \"Username\" FROM \"Users\" FOR UPDATE";
            var users = new List<(Guid Id, string Name)>();
            var names = new HashSet<string>(StringComparer.Ordinal);
            await using (var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false))
            {
                while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
                {
                    var name = reader.GetString(1).ToUpperInvariant();
                    if (!names.Add(name))
                    {
                        throw new InvalidOperationException("Cannot upgrade PostgreSQL: existing usernames collide after invariant normalization. Rename conflicting users before upgrading.");
                    }

                    users.Add((reader.GetGuid(0), name));
                }
            }

            command.CommandText = """
                ALTER TABLE "Users" ADD COLUMN IF NOT EXISTS "NormalizedUsername" character varying(255) NOT NULL DEFAULT '';
                DROP INDEX IF EXISTS "IX_Users_NormalizedUsername";
                """;
            await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
            command.CommandText = "UPDATE \"Users\" SET \"NormalizedUsername\" = @name WHERE \"Id\" = @id";
            var nameParameter = command.CreateParameter();
            nameParameter.ParameterName = "name";
            command.Parameters.Add(nameParameter);
            var idParameter = command.CreateParameter();
            idParameter.ParameterName = "id";
            command.Parameters.Add(idParameter);
            foreach (var user in users)
            {
                nameParameter.Value = user.Name;
                idParameter.Value = user.Id;
                await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
            }

            command.Parameters.Clear();
            command.CommandText = "CREATE UNIQUE INDEX \"IX_Users_NormalizedUsername\" ON \"Users\" (\"NormalizedUsername\")";
            await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            if (openedHere)
            {
                await connection.CloseAsync().ConfigureAwait(false);
            }
        }
    }

    /// <inheritdoc/>
    public void Initialise(DbContextOptionsBuilder options, DatabaseConfigurationOptions databaseConfiguration)
    {
        options.UseNpgsql(
            _dataSource,
            o => o.MigrationsAssembly(GetType().Assembly.FullName));
    }

    /// <inheritdoc/>
    public void OnModelCreating(ModelBuilder modelBuilder)
    {
    }

    /// <inheritdoc/>
    public void ConfigureConventions(ModelConfigurationBuilder configurationBuilder)
    {
    }

    /// <inheritdoc/>
    public async Task RunScheduledOptimisation(CancellationToken cancellationToken)
    {
        var context = await DbContextFactory!.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        await using (context.ConfigureAwait(false))
        {
            await context.Database.ExecuteSqlRawAsync("ANALYZE", cancellationToken).ConfigureAwait(false);
        }
    }

    /// <inheritdoc/>
    public Task RunShutdownTask(CancellationToken cancellationToken)
    {
        return Task.CompletedTask;
    }

    /// <inheritdoc/>
    public Task<string> MigrationBackupFast(CancellationToken cancellationToken)
    {
        // PostgreSQL pre-migration backups are handled externally by the
        // jellyfin-pg-backup CronJob. Return a sentinel so callers know no
        // file backup was created and the migration can proceed safely.
        return Task.FromResult(NoAutomatedBackupKey);
    }

    /// <inheritdoc/>
    public Task RestoreBackupFast(string key, CancellationToken cancellationToken)
    {
        // No automated backup was taken; nothing to restore.
        return Task.CompletedTask;
    }

    /// <inheritdoc/>
    public Task DeleteBackup(string key)
    {
        // No automated backup was taken; nothing to delete.
        return Task.CompletedTask;
    }

    /// <inheritdoc/>
    public async Task PurgeDatabase(JellyfinDbContext dbContext, IEnumerable<string>? tableNames)
    {
        ArgumentNullException.ThrowIfNull(tableNames);

        await dbContext.Database.ExecuteSqlRawAsync("SET session_replication_role = 'replica'").ConfigureAwait(false);
        try
        {
            foreach (var tableName in tableNames)
            {
                var truncateSql = "TRUNCATE TABLE \"" + tableName + "\" CASCADE";
                await dbContext.Database.ExecuteSqlRawAsync(truncateSql).ConfigureAwait(false);
            }
        }
        finally
        {
            await dbContext.Database.ExecuteSqlRawAsync("SET session_replication_role = 'origin'").ConfigureAwait(false);
        }
    }
}
