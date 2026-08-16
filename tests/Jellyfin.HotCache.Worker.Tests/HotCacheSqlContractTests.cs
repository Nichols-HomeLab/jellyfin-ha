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
        Assert.Equal([1, 2, 3, 4, 5, 6], HotCacheSchema.Migrations.Select(migration => migration.Version));
        Assert.Contains("DROP INDEX IF EXISTS hot_cache_jobs_claim_v2_idx", HotCacheSchema.Migrations.Single(migration => migration.Version == 5).Sql, StringComparison.Ordinal);
        Contains("manual cache and reconciliation audit history", HotCacheSchema.Migrations.Single(migration => migration.Version == 6).Name);
        Contains("SELECT 1 FROM hot_cache_schema_migrations WHERE version=@version", migrator);
        Contains("INSERT INTO hot_cache_schema_migrations(version) VALUES (@version)", migrator);
        Contains("pg_advisory_xact_lock", migrator);
    }

    [Fact]
    public void WorkerUsesNormalizedConfigurationPathsForKubernetesEnvironmentVariables()
    {
        var program = Read("Jellyfin.HotCache.Worker/Program.cs");

        Contains("Jellyfin:HotCache:CanonicalRoot", program);
        Contains("Jellyfin:HotCache:HotRoot", program);
        Contains("Jellyfin:HotCache:ObserveOnly", program);
        Contains("Jellyfin:HotCache:MetricsPort", program);
        Assert.DoesNotContain("Jellyfin__HotCache__CanonicalRoot", program, StringComparison.Ordinal);
    }

    [Fact]
    public void ReconciliationBuildsIndependentDeduplicatedTwoWeekPlaybackLists()
    {
        var coordinator = Read("Jellyfin.Server.Implementations/HotCache/PostgreSqlHotCacheCoordinator.cs");

        Contains("foreach (var user in _userManager.GetUsers())", coordinator);
        Contains("var cutoff = DateTime.UtcNow.Subtract(PlaybackInterestLifetime)", coordinator);
        Contains("FROM \"ActivityLogs\"", coordinator);
        Contains("\"Type\"='VideoPlayback'", coordinator);
        Contains("\"DateCreated\">=@cutoff", coordinator);
        Contains("\"UserId\"=@user", coordinator);
        Contains("JOIN LATERAL", coordinator);
        Contains("strpos(activity.\"Name\",item.\"SeriesName\"||' - '||item.\"Name\")>0", coordinator);
        Contains("ORDER BY MAX(activity.\"DateCreated\") DESC", coordinator);
        Contains("var newestBySeries = new HashSet<Guid>()", coordinator);
        Contains("DELETE FROM hot_cache_interests WHERE user_id=@user", coordinator);
        Contains("BOOL_OR(data.\"Played\")", coordinator);
        Contains("if (!recent.Played)", coordinator);
        Contains("\"recent-playback\", 100", coordinator);
    }

    [Fact]
    public void ReconciliationEvictsCompletedItemsAfterAllTwoWeekInterestsExpire()
    {
        var coordinator = Read("Jellyfin.Server.Implementations/HotCache/PostgreSqlHotCacheCoordinator.cs");

        Contains("j.state='completed' AND j.hot_path IS NOT NULL", coordinator);
        Contains("NOT EXISTS(SELECT 1 FROM hot_cache_interests interest", coordinator);
        Contains("NOT EXISTS(SELECT 1 FROM hot_cache_playback_leases lease", coordinator);
        Contains("SELECT id,'reconcile-release','two-week interests expired'", coordinator);
    }

    [Fact]
    public void AdministrationCountsOnlyMaterializedFilesAsCachedAndHidesReleasedHistory()
    {
        var administration = Read("Jellyfin.Server.Implementations/HotCache/PostgreSqlHotCacheAdministration.cs");

        Contains("state='completed' AND hot_path IS NOT NULL THEN 'copied'", administration);
        Contains("j.hot_path IS NOT NULL OR j.state IN ('pending','running') OR i.item_id IS NOT NULL", administration);
        Contains("ORDER BY COALESCE(j.series_name,''),COALESCE(j.episode_name,''),j.id", administration);
    }

    [Fact]
    public void InventoryEpisodeLabelsIncludeSeasonAndEpisodeNumbers()
    {
        var coordinator = Read("Jellyfin.Server.Implementations/HotCache/PostgreSqlHotCacheCoordinator.cs");

        Contains("return $\"S{season:00}E{number:00} · {item.Name}\";", coordinator);
        Contains("(\"episode\", BoundDisplay(EpisodeLabel(item)))", coordinator);
    }

    [Fact]
    public void CompletedEpisodeEvictsOnlyWhenNoUserOrPlaybackStillHoldsIt()
    {
        var coordinator = Read("Jellyfin.Server.Implementations/HotCache/PostgreSqlHotCacheCoordinator.cs");

        Contains("ReleaseCompletedEpisodeAsync", coordinator);
        Contains("WHERE item_id=@item AND user_id=@user", coordinator);
        Contains("NOT EXISTS(SELECT 1 FROM hot_cache_interests interest", coordinator);
        Contains("NOT EXISTS(SELECT 1 FROM hot_cache_playback_leases lease", coordinator);
        Contains("kind='eviction',state='pending'", coordinator);
        Contains("PlaybackCurrentInterestLifetime", coordinator);
        Contains("playback is PlaybackStopEventArgs { PlayedToCompletion: true }", coordinator);
        Contains("RetainStoppedEpisodeAsync", coordinator);
    }

    [Fact]
    public void PlaybackLookaheadStaysInTheCurrentSeason()
    {
        var coordinator = Read("Jellyfin.Server.Implementations/HotCache/PostgreSqlHotCacheCoordinator.cs");

        // House S1E2 must select S1E3 onward, never an unrelated Cops episode.
        Contains("ParentId = episode.SeasonId", coordinator);
        Contains("MinParentAndIndexNumber = (episode.ParentIndexNumber ?? 0, (episode.IndexNumber ?? 0) + 1)", coordinator);
        Contains("Limit = lookahead", coordinator);
        Contains("if (lifecycle == HotCachePlaybackEvent.Started)", coordinator);
    }

    [Fact]
    public void PlaybackLookaheadSkipsEpisodesAlreadyWatchedByThatUser()
    {
        var coordinator = Read("Jellyfin.Server.Implementations/HotCache/PostgreSqlHotCacheCoordinator.cs");

        Contains("var user = _userManager.GetUserById(userId)", coordinator);
        Contains("new InternalItemsQuery(user)", coordinator);
        Contains("IsPlayed = false", coordinator);
        Contains("no following unwatched episodes in season", coordinator);
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
