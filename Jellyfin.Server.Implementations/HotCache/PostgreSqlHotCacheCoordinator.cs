using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;
using Jellyfin.Data.Enums;
using Jellyfin.Database.Implementations.Enums;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Library;
using MediaBrowser.Model.Entities;
using Microsoft.Extensions.Logging;
using Npgsql;

namespace Jellyfin.Server.Implementations.HotCache;

/// <summary>PostgreSQL authority for hot-cache interests, playback leases, and worker observations.</summary>
public sealed class PostgreSqlHotCacheCoordinator : IHotCacheCoordinator
{
    private static readonly TimeSpan PlaybackLeaseLifetime = TimeSpan.FromMinutes(2);
    private static readonly TimeSpan PlaybackCurrentInterestLifetime = TimeSpan.FromMinutes(2);
    private static readonly TimeSpan PlaybackInterestLifetime = TimeSpan.FromDays(14);
    private readonly NpgsqlDataSource _dataSource;
    private readonly IUserManager _userManager;
    private readonly ILibraryManager _libraryManager;
    private readonly ILogger<PostgreSqlHotCacheCoordinator> _logger;
    private readonly Channel<ResolutionObservation> _observations = Channel.CreateBounded<ResolutionObservation>(new BoundedChannelOptions(1024) { FullMode = BoundedChannelFullMode.DropWrite });

    /// <summary>
    /// Initializes a new instance of the <see cref="PostgreSqlHotCacheCoordinator"/> class.
    /// </summary>
    /// <param name="dataSource">The shared PostgreSQL data source.</param>
    /// <param name="userManager">The Jellyfin user source.</param>
    /// <param name="libraryManager">The Jellyfin library query source.</param>
    /// <param name="logger">The cold-degradation diagnostic logger.</param>
    public PostgreSqlHotCacheCoordinator(NpgsqlDataSource dataSource, IUserManager userManager, ILibraryManager libraryManager, ILogger<PostgreSqlHotCacheCoordinator> logger)
    {
        _dataSource = dataSource;
        _userManager = userManager;
        _libraryManager = libraryManager;
        _logger = logger;
    }

    /// <inheritdoc />
    public async Task RecordPlaybackAsync(PlaybackProgressEventArgs playback, HotCachePlaybackEvent lifecycle, CancellationToken cancellationToken)
    {
        if (playback.Item is not MediaBrowser.Controller.Entities.TV.Episode episode
            || string.IsNullOrWhiteSpace(episode.Path)
            || !Path.IsPathFullyQualified(episode.Path)
            || playback.Users is null
            || playback.Users.Count == 0)
        {
            return;
        }

        var users = playback.Users.ToArray();

        try
        {
            await using var connection = await _dataSource.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
            await using var transaction = await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
            if (lifecycle == HotCachePlaybackEvent.Stopped)
            {
                if (!string.IsNullOrWhiteSpace(playback.PlaySessionId))
                {
                    await ExecuteAsync(connection, transaction, "DELETE FROM hot_cache_playback_leases WHERE play_session_id=@session", cancellationToken, ("session", playback.PlaySessionId)).ConfigureAwait(false);
                }

                foreach (var user in users)
                {
                    await ReleaseCompletedEpisodeAsync(connection, transaction, playback.Item.Id, user.Id, cancellationToken).ConfigureAwait(false);
                }
            }
            else
            {
                // Serializes playback activation with the worker's short unlink transaction.
                await ExecuteAsync(connection, transaction, "SELECT 1 FROM hot_cache_jobs WHERE item_id=@item FOR UPDATE", cancellationToken, ("item", playback.Item.Id)).ConfigureAwait(false);
                if (!string.IsNullOrWhiteSpace(playback.PlaySessionId))
                {
                    await ExecuteAsync(connection, transaction, "INSERT INTO hot_cache_playback_leases(play_session_id,item_id,expires_at_utc) VALUES(@session,@item,now()+@lease) ON CONFLICT(play_session_id) DO UPDATE SET item_id=excluded.item_id,expires_at_utc=excluded.expires_at_utc,updated_at_utc=now()", cancellationToken, ("session", playback.PlaySessionId), ("item", playback.Item.Id), ("lease", PlaybackLeaseLifetime)).ConfigureAwait(false);
                }

                foreach (var user in users)
                {
                    await ExecuteAsync(connection, transaction, "INSERT INTO hot_cache_interests(item_id,user_id,reason,priority,expires_at_utc) VALUES(@item,@user,'playback',100,now()+@expiry) ON CONFLICT(item_id,user_id,reason) DO UPDATE SET priority=excluded.priority,expires_at_utc=excluded.expires_at_utc,last_observed_utc=now()", cancellationToken, ("item", playback.Item.Id), ("user", user.Id), ("expiry", PlaybackCurrentInterestLifetime)).ConfigureAwait(false);
                    await UpsertJobAsync(connection, transaction, playback.Item, 100, cancellationToken).ConfigureAwait(false);
                    if (lifecycle == HotCachePlaybackEvent.Started)
                    {
                        await QueueFollowingEpisodesAsync(connection, transaction, episode, user.Id, await GetEffectiveLookaheadAsync(connection, transaction, cancellationToken).ConfigureAwait(false), cancellationToken).ConfigureAwait(false);
                    }
                }

                await ExecuteAsync(connection, transaction, "UPDATE hot_cache_jobs SET is_active=true,last_access_utc=now(),updated_at=now() WHERE item_id=@item", cancellationToken, ("item", playback.Item.Id)).ConfigureAwait(false);
            }

            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (!cancellationToken.IsCancellationRequested)
        {
            _logger.LogWarning(ex, "Hot-cache playback coordination is unavailable; playback continues from canonical storage.");
        }
    }

    /// <inheritdoc />
    public async Task ReconcileAsync(CancellationToken cancellationToken)
    {
        await using var connection = await _dataSource.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        await using var lockCommand = new NpgsqlCommand("SELECT pg_try_advisory_xact_lock(8430169)", connection, transaction);
        if (!(bool)(await lockCommand.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false) ?? false))
        {
            return;
        }

        foreach (var user in _userManager.GetUsers())
        {
            await ReconcileUserAsync(connection, transaction, user, cancellationToken).ConfigureAwait(false);
        }

        await ExecuteAsync(
            connection,
            transaction,
            """
            DELETE FROM hot_cache_interests WHERE expires_at_utc <= now();
            DELETE FROM hot_cache_playback_leases WHERE expires_at_utc <= now();
            UPDATE hot_cache_jobs j
            SET priority=COALESCE((SELECT MAX(interest.priority) FROM hot_cache_interests interest WHERE interest.item_id=j.item_id AND interest.expires_at_utc>now()),0),
                is_active=EXISTS(SELECT 1 FROM hot_cache_playback_leases lease WHERE lease.item_id=j.item_id AND lease.expires_at_utc>now()),
                updated_at=now()
            WHERE j.state IN ('pending','running','completed');
            UPDATE hot_cache_jobs j
            SET state='completed',priority=0,lease_owner=NULL,lease_expires_at=NULL,updated_at=now()
            WHERE j.state='pending' AND j.hot_path IS NULL AND NOT j.is_pinned
              AND NOT EXISTS(SELECT 1 FROM hot_cache_interests interest WHERE interest.item_id=j.item_id AND interest.expires_at_utc>now());
            WITH evicted AS (
                UPDATE hot_cache_jobs j
                SET kind='eviction',state='pending',priority=0,is_active=false,lease_owner=NULL,lease_expires_at=NULL,updated_at=now()
                WHERE j.state='completed' AND j.hot_path IS NOT NULL AND NOT j.is_pinned
                  AND NOT EXISTS(SELECT 1 FROM hot_cache_interests interest WHERE interest.item_id=j.item_id AND interest.expires_at_utc>now())
                  AND NOT EXISTS(SELECT 1 FROM hot_cache_playback_leases lease WHERE lease.item_id=j.item_id AND lease.expires_at_utc>now())
                RETURNING j.id)
            INSERT INTO hot_cache_events(job_id,kind,detail)
            SELECT id,'reconcile-release','two-week interests expired' FROM evicted;
            """,
            cancellationToken).ConfigureAwait(false);
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task<int> CacheLibraryItemAsync(Guid itemId, bool includeSeason, CancellationToken cancellationToken)
    {
        var item = _libraryManager.GetItemById(itemId);
        if (item is null)
        {
            return 0;
        }

        var episodes = includeSeason
            ? _libraryManager.GetItemList(new InternalItemsQuery { IncludeItemTypes = [BaseItemKind.Episode], ParentId = itemId, OrderBy = [(ItemSortBy.ParentIndexNumber, SortOrder.Ascending), (ItemSortBy.IndexNumber, SortOrder.Ascending)] })
            : [item];
        await using var connection = await _dataSource.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        var accepted = 0;
        foreach (var episode in episodes)
        {
            if (episode is not MediaBrowser.Controller.Entities.TV.Episode || string.IsNullOrWhiteSpace(episode.Path) || !Path.IsPathFullyQualified(episode.Path))
            {
                continue;
            }

            await RecordCandidateAsync(connection, transaction, episode, Guid.Empty, "manual", 100, cancellationToken).ConfigureAwait(false);
            await LogReconcileAsync(connection, transaction, $"manual queue: {Describe(episode)}", cancellationToken).ConfigureAwait(false);
            accepted++;
        }

        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        return accepted;
    }

    /// <inheritdoc />
    public void ObserveResolution(in PlaybackPathRequest request, in PlaybackPathResolution resolution)
    {
        if (!resolution.IsHot && resolution.Reason != "hot-miss")
        {
            _logger.LogWarning("Hot-cache playback degraded to canonical media. {Reason} {Purpose}", resolution.Reason, request.Purpose);
        }

        _observations.Writer.TryWrite(new ResolutionObservation(request.CanonicalPath, resolution.Reason, resolution.IsHot));
    }

    /// <summary>Creates the additive schema shared with the worker queue from issue 70.</summary>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>A task that completes when the schema is available.</returns>
    public async Task EnsureMigratedAsync(CancellationToken cancellationToken)
    {
        await PostgreSqlHotCacheSchemaMigrator.ApplyAsync(_dataSource, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Persists bounded resolver observations without adding a database round trip to playback.</summary>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>A task that completes when the pending observations are drained.</returns>
    public async Task DrainObservationsAsync(CancellationToken cancellationToken)
    {
        while (_observations.Reader.TryRead(out var observation))
        {
            await using var command = _dataSource.CreateCommand("""
                WITH target AS (SELECT id FROM hot_cache_jobs WHERE canonical_path=@path ORDER BY updated_at DESC LIMIT 1),
                touch AS (UPDATE hot_cache_jobs SET last_access_utc=now(),updated_at=now() WHERE id=(SELECT id FROM target) AND @hot RETURNING id),
                repair AS (UPDATE hot_cache_jobs SET kind='promotion',state='pending',lease_owner=NULL,lease_expires_at=NULL,updated_at=now() WHERE id=(SELECT id FROM target) AND @repair AND state <> 'running' AND EXISTS(SELECT 1 FROM hot_cache_interests interest WHERE interest.item_id=hot_cache_jobs.item_id AND interest.expires_at_utc>now()) RETURNING id)
                INSERT INTO hot_cache_events(job_id,kind,detail) SELECT id,@kind,@detail FROM target;
                """);
            command.Parameters.AddWithValue("kind", observation.IsHot ? "playback-hit" : "validate-or-repair");
            command.Parameters.AddWithValue("detail", observation.Reason);
            command.Parameters.AddWithValue("path", observation.CanonicalPath);
            command.Parameters.AddWithValue("hot", observation.IsHot);
            command.Parameters.AddWithValue("repair", !observation.IsHot);
            await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }
    }

    private static async Task ExecuteAsync(NpgsqlConnection connection, NpgsqlTransaction transaction, string sql, CancellationToken cancellationToken, params (string Name, object Value)[] parameters)
    {
        await using var command = new NpgsqlCommand(sql, connection, transaction);
        foreach (var parameter in parameters)
        {
            command.Parameters.AddWithValue(parameter.Name, parameter.Value);
        }

        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    private async Task ReconcileUserAsync(NpgsqlConnection connection, NpgsqlTransaction transaction, Jellyfin.Database.Implementations.Entities.User user, CancellationToken cancellationToken)
    {
        // This is the user's independent cache list. Refreshing it must never
        // delete another user's interest in the same episode.
        await ExecuteAsync(connection, transaction, "DELETE FROM hot_cache_interests WHERE user_id=@user AND reason IN ('next-up','next-episode')", cancellationToken, ("user", user.Id)).ConfigureAwait(false);

        var cutoff = DateTime.UtcNow.Subtract(PlaybackInterestLifetime);
        var newestBySeries = new HashSet<Guid>();
        var selected = 0;
        var lookahead = await GetEffectiveLookaheadAsync(connection, transaction, cancellationToken).ConfigureAwait(false);
        await using var recentCommand = new NpgsqlCommand(
            """
                SELECT resolved."Id"::text
                FROM "ActivityLogs" activity
                JOIN LATERAL (
                    SELECT item."Id"
                    FROM "BaseItems" item
                    WHERE item."SeriesName" IS NOT NULL
                      AND item."Path" IS NOT NULL
                      AND (replace(item."Id"::text,'-','')=replace(lower(activity."ItemId"),'-','')
                           OR strpos(activity."Name",item."SeriesName"||' - '||item."Name")>0)
                    ORDER BY CASE WHEN replace(item."Id"::text,'-','')=replace(lower(activity."ItemId"),'-','') THEN 0 ELSE 1 END,
                             length(item."SeriesName"||' - '||item."Name") DESC
                    LIMIT 1) resolved ON true
                WHERE activity."UserId"=@user
                  AND activity."Type"='VideoPlayback'
                  AND activity."DateCreated">=@cutoff
                GROUP BY resolved."Id"
                ORDER BY MAX(activity."DateCreated") DESC
                """,
            connection,
            transaction);
        recentCommand.Parameters.AddWithValue("user", user.Id);
        recentCommand.Parameters.AddWithValue("cutoff", cutoff);
        var recentItemIds = new List<Guid>();
        await using (var reader = await recentCommand.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false))
        {
            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                if (Guid.TryParse(reader.GetString(0), out var itemId))
                {
                    recentItemIds.Add(itemId);
                }
            }
        }

        foreach (var itemId in recentItemIds)
        {
            if (_libraryManager.GetItemById(itemId) is not MediaBrowser.Controller.Entities.TV.Episode episode
                || !newestBySeries.Add(episode.SeriesId))
            {
                continue;
            }

            selected++;
            await QueueFollowingEpisodesAsync(connection, transaction, episode, user.Id, lookahead, cancellationToken).ConfigureAwait(false);
        }

        await LogReconcileAsync(connection, transaction, $"scan user={BoundDisplay(user.Username)}; recent-series={selected}; lookahead={lookahead}", cancellationToken).ConfigureAwait(false);
    }

    private static async Task ReleaseCompletedEpisodeAsync(NpgsqlConnection connection, NpgsqlTransaction transaction, Guid itemId, Guid userId, CancellationToken cancellationToken)
    {
        const string sql = """
            WITH released AS (
                DELETE FROM hot_cache_interests
                WHERE item_id=@item AND user_id=@user AND reason IN ('playback','next-up','next-episode')
            ), evicted AS (
                UPDATE hot_cache_jobs j
                SET kind='eviction',state='pending',priority=0,is_active=false,lease_owner=NULL,lease_expires_at=NULL,updated_at=now()
                WHERE j.item_id=@item AND j.state='completed' AND j.hot_path IS NOT NULL AND NOT j.is_pinned
                  AND NOT EXISTS(SELECT 1 FROM hot_cache_interests interest WHERE interest.item_id=j.item_id AND interest.expires_at_utc>now())
                  AND NOT EXISTS(SELECT 1 FROM hot_cache_playback_leases lease WHERE lease.item_id=j.item_id AND lease.expires_at_utc>now())
                RETURNING j.id)
            INSERT INTO hot_cache_events(job_id,kind,detail)
            SELECT id,'release','all user interests completed' FROM evicted;
            """;
        await ExecuteAsync(connection, transaction, sql, cancellationToken, ("item", itemId), ("user", userId)).ConfigureAwait(false);
    }

    // The configured maximum is six by default. Capacity independently limits
    // lookahead from the currently selected backend, leaving the reserve intact.
    private static async Task<int> GetEffectiveLookaheadAsync(NpgsqlConnection connection, NpgsqlTransaction transaction, CancellationToken cancellationToken)
    {
        const string sql = "SELECT GREATEST(1, LEAST(6, s.max_lookahead, GREATEST(1, FLOOR(GREATEST(0, COALESCE((SELECT available_bytes FROM hot_cache_backend_observations WHERE backend=s.backend AND healthy ORDER BY observed_at DESC LIMIT 1), 0) - s.reserve_free_bytes) / GREATEST(COALESCE((SELECT AVG(source_length) FROM hot_cache_jobs WHERE source_length > 0), 1), 1)))))::integer FROM hot_cache_settings s WHERE s.id=true";
        await using var command = new NpgsqlCommand(sql, connection, transaction);
        return (await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false) as int?) ?? 0;
    }

    private async Task RecordCandidateAsync(NpgsqlConnection connection, NpgsqlTransaction transaction, BaseItem item, Guid userId, string reason, int priority, CancellationToken cancellationToken)
    {
        if (item is not MediaBrowser.Controller.Entities.TV.Episode || string.IsNullOrWhiteSpace(item.Path) || !Path.IsPathFullyQualified(item.Path))
        {
            return;
        }

        await ExecuteAsync(connection, transaction, "INSERT INTO hot_cache_interests(item_id,user_id,reason,priority,expires_at_utc) VALUES(@item,@user,@reason,@priority,now()+@expiry) ON CONFLICT(item_id,user_id,reason) DO UPDATE SET priority=excluded.priority,expires_at_utc=excluded.expires_at_utc,last_observed_utc=now()", cancellationToken, ("item", item.Id), ("user", userId), ("reason", reason), ("priority", priority), ("expiry", PlaybackInterestLifetime)).ConfigureAwait(false);
        await UpsertJobAsync(connection, transaction, item, priority, cancellationToken).ConfigureAwait(false);
    }

    private async Task QueueFollowingEpisodesAsync(NpgsqlConnection connection, NpgsqlTransaction transaction, MediaBrowser.Controller.Entities.TV.Episode episode, Guid userId, int lookahead, CancellationToken cancellationToken)
    {
        var user = _userManager.GetUserById(userId);
        if (user is null)
        {
            await LogReconcileAsync(connection, transaction, $"skip {Describe(episode)}: user no longer exists", cancellationToken).ConfigureAwait(false);
            return;
        }

        var following = _libraryManager.GetItemList(new InternalItemsQuery(user)
        {
            IncludeItemTypes = [BaseItemKind.Episode],
            ParentId = episode.SeasonId,
            MinParentAndIndexNumber = (episode.ParentIndexNumber ?? 0, (episode.IndexNumber ?? 0) + 1),
            IsPlayed = false,
            Limit = lookahead,
            OrderBy = [(ItemSortBy.ParentIndexNumber, SortOrder.Ascending), (ItemSortBy.IndexNumber, SortOrder.Ascending)]
        });
        if (following.Count == 0)
        {
            await LogReconcileAsync(connection, transaction, $"skip {Describe(episode)}: no following unwatched episodes in season", cancellationToken).ConfigureAwait(false);
            return;
        }

        var priority = 80;
        foreach (var next in following)
        {
            await RecordCandidateAsync(connection, transaction, next, userId, priority == 80 ? "next-up" : "next-episode", priority, cancellationToken).ConfigureAwait(false);
            await LogReconcileAsync(connection, transaction, $"queue {(priority == 80 ? "next-up" : "next-episode")}: {Describe(next)}", cancellationToken).ConfigureAwait(false);
            priority -= 10;
        }
    }

    private static Task LogReconcileAsync(NpgsqlConnection connection, NpgsqlTransaction transaction, string detail, CancellationToken cancellationToken)
        => ExecuteAsync(connection, transaction, "INSERT INTO hot_cache_admin_history(kind,detail) VALUES('reconcile',@detail)", cancellationToken, ("detail", BoundDisplay(detail)));

    private static string Describe(BaseItem item) => BoundDisplay($"{(item is IHasSeries series && !string.IsNullOrWhiteSpace(series.SeriesName) ? series.SeriesName : item.Name)} — {item.Name}");

    private static Task UpsertJobAsync(NpgsqlConnection connection, NpgsqlTransaction transaction, BaseItem item, int priority, CancellationToken cancellationToken)
    {
        var source = new FileInfo(item.Path);
        return ExecuteAsync(connection, transaction, "INSERT INTO hot_cache_jobs(id,kind,state,item_id,canonical_path,source_length,source_modified_utc,priority,series_name,episode_name) VALUES(@id,'promotion','pending',@item,@path,@length,@mtime,@priority,@series,@episode) ON CONFLICT(item_id) WHERE item_id IS NOT NULL DO UPDATE SET priority=GREATEST(hot_cache_jobs.priority,excluded.priority),canonical_path=excluded.canonical_path,source_length=excluded.source_length,source_modified_utc=excluded.source_modified_utc,series_name=excluded.series_name,episode_name=excluded.episode_name,kind=CASE WHEN hot_cache_jobs.state <> 'running' AND hot_cache_jobs.hot_path IS NULL THEN 'promotion' ELSE hot_cache_jobs.kind END,state=CASE WHEN hot_cache_jobs.state <> 'running' AND hot_cache_jobs.hot_path IS NULL THEN 'pending' ELSE hot_cache_jobs.state END,lease_owner=CASE WHEN hot_cache_jobs.state <> 'running' AND hot_cache_jobs.hot_path IS NULL THEN NULL ELSE hot_cache_jobs.lease_owner END,lease_expires_at=CASE WHEN hot_cache_jobs.state <> 'running' AND hot_cache_jobs.hot_path IS NULL THEN NULL ELSE hot_cache_jobs.lease_expires_at END,updated_at=now()", cancellationToken, ("id", Guid.NewGuid()), ("item", item.Id), ("path", item.Path), ("length", source.Exists ? source.Length : 0L), ("mtime", source.Exists ? source.LastWriteTimeUtc : DateTime.UnixEpoch), ("priority", priority), ("series", BoundDisplay(item is IHasSeries series && !string.IsNullOrWhiteSpace(series.SeriesName) ? series.SeriesName : item.Name)), ("episode", BoundDisplay(item.Name)));
    }

    private static string BoundDisplay(string value) => value.Length <= 512 ? value : value[..512];

    private readonly record struct ResolutionObservation(string CanonicalPath, string Reason, bool IsHot);
}
