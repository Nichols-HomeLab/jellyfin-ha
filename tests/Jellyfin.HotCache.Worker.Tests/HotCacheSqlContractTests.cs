using System.IO;
using MediaBrowser.Controller.Library;
using Xunit;

namespace Jellyfin.HotCache.Worker.Tests;

/// <summary>
/// Guards PostgreSQL state-machine guarantees without requiring a mutable PostgreSQL instance.
/// The production SQL is intentionally asserted here rather than reimplemented in a test double.
/// </summary>
public sealed class HotCacheSqlContractTests
{
    [Fact]
    public void ClaimAndCompletionRequireAValidOwnerLease()
    {
        var store = Read("Jellyfin.HotCache.Worker/PostgreSqlHotCacheJobStore.cs");

        Contains("FOR UPDATE SKIP LOCKED", store);
        Contains("state = 'running' AND lease_expires_at < now()", store);
        Contains("lease_owner=@owner AND lease_expires_at>now()", store);
        Contains("state='completed',is_copying=false", store);
        Contains("hot_path=CASE WHEN kind='eviction' THEN NULL ELSE COALESCE(@hotPath,hot_path) END", store);
        Contains("lease_owner=NULL,lease_expires_at=NULL", store);
    }

    [Fact]
    public void EvictionRechecksProtectionAndExpiredPlaybackLeases()
    {
        var store = Read("Jellyfin.HotCache.Worker/PostgreSqlHotCacheJobStore.cs");

        Contains("NOT is_active AND NOT is_pinned AND NOT is_copying AND priority <= 0", store);
        Contains("l.expires_at_utc>now()", store);
        Contains("TryEvictAsync", store);
        Contains("FOR UPDATE", store);
        Contains("state='running' AND lease_owner=@owner AND lease_expires_at>now()", store);
        Contains("DeferEvictionAsync", store);
    }

    [Fact]
    public void RetentionAndSchemaMigrationsAreOrderedAndLedgerGuarded()
    {
        var store = Read("Jellyfin.HotCache.Worker/PostgreSqlHotCacheJobStore.cs");
        var migrator = Read("Jellyfin.Server.Implementations/HotCache/PostgreSqlHotCacheSchemaMigrator.cs");

        Contains("DELETE FROM hot_cache_events WHERE created_at < now() - interval '30 days'", store);
        Contains("DELETE FROM hot_cache_admin_history WHERE created_at < now() - interval '30 days'", store);
        Contains("CREATE TABLE IF NOT EXISTS hot_cache_schema_migrations", HotCacheSchema.CreateLedgerSql);
        Assert.Equal([1, 2, 3, 4, 5], HotCacheSchema.Migrations.Select(migration => migration.Version));
        Assert.Contains("DROP INDEX IF EXISTS hot_cache_jobs_claim_v2_idx", HotCacheSchema.Migrations.Single(migration => migration.Version == 5).Sql, StringComparison.Ordinal);
        Contains("SELECT 1 FROM hot_cache_schema_migrations WHERE version=@version", migrator);
        Contains("INSERT INTO hot_cache_schema_migrations(version) VALUES (@version)", migrator);
        Contains("pg_advisory_xact_lock", migrator);
    }

    private static string Read(string relativePath)
    {
        var directory = AppContext.BaseDirectory;
        while (directory is not null)
        {
            var candidate = Path.Combine(directory, relativePath);
            if (File.Exists(candidate))
            {
                return File.ReadAllText(candidate);
            }

            directory = Directory.GetParent(directory)?.FullName;
        }

        throw new DirectoryNotFoundException($"Repository source was not found for {relativePath}.");
    }

    private static void Contains(string expected, string actual)
        => Assert.Contains(expected, actual, StringComparison.Ordinal);
}
