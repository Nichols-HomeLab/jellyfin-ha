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
    private static readonly TimeSpan PlaybackInterestLifetime = TimeSpan.FromDays(14);
    private readonly NpgsqlDataSource _dataSource;
    private readonly IUserManager _userManager;
    private readonly ILibraryManager _libraryManager;
    private readonly IUserDataManager _userDataManager;
    private readonly HashSet<Guid>? _includedUsers;
    private readonly ILogger<PostgreSqlHotCacheCoordinator> _logger;
    private readonly Channel<ResolutionObservation> _observations = Channel.CreateBounded<ResolutionObservation>(new BoundedChannelOptions(1024) { FullMode = BoundedChannelFullMode.DropWrite });

    /// <summary>
    /// Initializes a new instance of the <see cref="PostgreSqlHotCacheCoordinator"/> class.
    /// </summary>
    /// <param name="dataSource">The shared PostgreSQL data source.</param>
    /// <param name="userManager">The Jellyfin user source.</param>
    /// <param name="libraryManager">The Jellyfin library query source.</param>
    /// <param name="userDataManager">The per-user playback state source.</param>
    /// <param name="logger">The cold-degradation diagnostic logger.</param>
    public PostgreSqlHotCacheCoordinator(NpgsqlDataSource dataSource, IUserManager userManager, ILibraryManager libraryManager, IUserDataManager userDataManager, ILogger<PostgreSqlHotCacheCoordinator> logger)
    {
        _dataSource = dataSource;
        _userManager = userManager;
        _libraryManager = libraryManager;
        _userDataManager = userDataManager;
        _includedUsers = ParseIncludedUsers(Environment.GetEnvironmentVariable("JELLYFIN_HOT_CACHE_INCLUDED_USER_IDS"));
        _logger = logger;
    }

    /// <inheritdoc />
    public async Task RecordPlaybackAsync(PlaybackProgressEventArgs playback, HotCachePlaybackEvent lifecycle, CancellationToken cancellationToken)
    {
        if (playback.Item is null || playback.Users is null || playback.Users.Count == 0)
        {
            return;
        }

        var users = playback.Users.Where(IsIncluded).ToArray();
        if (users.Length == 0)
        {
            return;
        }

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
            }
            else
            {
                if (!string.IsNullOrWhiteSpace(playback.PlaySessionId))
                {
                    await ExecuteAsync(connection, transaction, "INSERT INTO hot_cache_playback_leases(play_session_id,item_id,expires_at_utc) VALUES(@session,@item,now()+@lease) ON CONFLICT(play_session_id) DO UPDATE SET item_id=excluded.item_id,expires_at_utc=excluded.expires_at_utc,updated_at_utc=now()", cancellationToken, ("session", playback.PlaySessionId), ("item", playback.Item.Id), ("lease", PlaybackLeaseLifetime)).ConfigureAwait(false);
                }

                foreach (var user in users)
                {
                    await ExecuteAsync(connection, transaction, "INSERT INTO hot_cache_interests(item_id,user_id,reason,priority,expires_at_utc) VALUES(@item,@user,'playback',100,now()+@expiry) ON CONFLICT(item_id,user_id,reason) DO UPDATE SET priority=excluded.priority,expires_at_utc=excluded.expires_at_utc,last_observed_utc=now()", cancellationToken, ("item", playback.Item.Id), ("user", user.Id), ("expiry", PlaybackInterestLifetime)).ConfigureAwait(false);
                    await UpsertJobAsync(connection, transaction, playback.Item, 100, cancellationToken).ConfigureAwait(false);
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

        foreach (var user in _userManager.GetUsers().Where(IsIncluded))
        {
            await ReconcileUserAsync(connection, transaction, user, cancellationToken).ConfigureAwait(false);
        }

        await ExecuteAsync(connection, transaction, "DELETE FROM hot_cache_interests WHERE expires_at_utc <= now(); DELETE FROM hot_cache_playback_leases WHERE expires_at_utc <= now(); UPDATE hot_cache_jobs j SET priority=COALESCE((SELECT MAX(interest.priority) FROM hot_cache_interests interest WHERE interest.item_id=j.item_id AND interest.expires_at_utc>now()),0),is_active=EXISTS(SELECT 1 FROM hot_cache_playback_leases lease WHERE lease.item_id=j.item_id AND lease.expires_at_utc>now()),updated_at=now() WHERE j.state IN ('pending','running','completed');", cancellationToken).ConfigureAwait(false);
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
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
        await using var command = _dataSource.CreateCommand(HotCacheSchema.Sql);
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
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
                repair AS (UPDATE hot_cache_jobs SET kind='promotion',state='pending',lease_owner=NULL,lease_expires_at=NULL,updated_at=now() WHERE id=(SELECT id FROM target) AND @repair AND state <> 'running' RETURNING id)
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
        var resume = _libraryManager.GetItemList(new InternalItemsQuery(user)
        {
            IncludeItemTypes = [BaseItemKind.Episode],
            IsResumable = true,
            Limit = 50,
            OrderBy = [(ItemSortBy.DatePlayed, SortOrder.Descending)]
        });
        var recent = _libraryManager.GetItemList(new InternalItemsQuery(user)
        {
            IncludeItemTypes = [BaseItemKind.Episode],
            IsPlayed = true,
            Limit = 50,
            OrderBy = [(ItemSortBy.DatePlayed, SortOrder.Descending)]
        });

        foreach (var item in resume)
        {
            await RecordCandidateAsync(connection, transaction, item, user.Id, "continue-watching", 90, cancellationToken).ConfigureAwait(false);
        }

        var reconciledSeries = new HashSet<Guid>();
        foreach (var item in recent)
        {
            var userData = _userDataManager.GetUserData(user, item);
            if (userData?.LastPlayedDate is not DateTime lastPlayed || lastPlayed < DateTime.UtcNow.Subtract(PlaybackInterestLifetime))
            {
                continue;
            }

            if (item is MediaBrowser.Controller.Entities.TV.Episode episode)
            {
                if (!reconciledSeries.Add(episode.SeriesId))
                {
                    continue;
                }

                await RecordCandidateAsync(connection, transaction, item, user.Id, "recent-series", 10, cancellationToken).ConfigureAwait(false);
                var following = _libraryManager.GetItemList(new InternalItemsQuery(user)
                {
                    IncludeItemTypes = [BaseItemKind.Episode],
                    ParentId = episode.SeasonId,
                    MinParentAndIndexNumber = (episode.ParentIndexNumber ?? 0, (episode.IndexNumber ?? 0) + 1),
                    Limit = 3,
                    OrderBy = [(ItemSortBy.SortName, SortOrder.Ascending)]
                });
                var priority = 80;
                foreach (var next in following)
                {
                    await RecordCandidateAsync(connection, transaction, next, user.Id, priority == 80 ? "next-up" : "next-episode", priority, cancellationToken).ConfigureAwait(false);
                    priority -= 10;
                }
            }
        }
    }

    private async Task RecordCandidateAsync(NpgsqlConnection connection, NpgsqlTransaction transaction, BaseItem item, Guid userId, string reason, int priority, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(item.Path))
        {
            return;
        }

        await ExecuteAsync(connection, transaction, "INSERT INTO hot_cache_interests(item_id,user_id,reason,priority,expires_at_utc) VALUES(@item,@user,@reason,@priority,now()+@expiry) ON CONFLICT(item_id,user_id,reason) DO UPDATE SET priority=excluded.priority,expires_at_utc=excluded.expires_at_utc,last_observed_utc=now()", cancellationToken, ("item", item.Id), ("user", userId), ("reason", reason), ("priority", priority), ("expiry", PlaybackInterestLifetime)).ConfigureAwait(false);
        await UpsertJobAsync(connection, transaction, item, priority, cancellationToken).ConfigureAwait(false);
    }

    private static Task UpsertJobAsync(NpgsqlConnection connection, NpgsqlTransaction transaction, BaseItem item, int priority, CancellationToken cancellationToken)
    {
        var source = new FileInfo(item.Path);
        return ExecuteAsync(connection, transaction, "INSERT INTO hot_cache_jobs(id,kind,state,item_id,canonical_path,source_length,source_modified_utc,priority,series_name,episode_name) VALUES(@id,'promotion','pending',@item,@path,@length,@mtime,@priority,@series,@episode) ON CONFLICT(item_id) WHERE item_id IS NOT NULL DO UPDATE SET priority=GREATEST(hot_cache_jobs.priority,excluded.priority),canonical_path=excluded.canonical_path,source_length=excluded.source_length,source_modified_utc=excluded.source_modified_utc,series_name=excluded.series_name,episode_name=excluded.episode_name,kind=CASE WHEN hot_cache_jobs.state <> 'running' AND hot_cache_jobs.hot_path IS NULL THEN 'promotion' ELSE hot_cache_jobs.kind END,state=CASE WHEN hot_cache_jobs.state <> 'running' AND hot_cache_jobs.hot_path IS NULL THEN 'pending' ELSE hot_cache_jobs.state END,lease_owner=CASE WHEN hot_cache_jobs.state <> 'running' AND hot_cache_jobs.hot_path IS NULL THEN NULL ELSE hot_cache_jobs.lease_owner END,lease_expires_at=CASE WHEN hot_cache_jobs.state <> 'running' AND hot_cache_jobs.hot_path IS NULL THEN NULL ELSE hot_cache_jobs.lease_expires_at END,updated_at=now()", cancellationToken, ("id", Guid.NewGuid()), ("item", item.Id), ("path", item.Path), ("length", source.Exists ? source.Length : 0L), ("mtime", source.Exists ? source.LastWriteTimeUtc : DateTime.UnixEpoch), ("priority", priority), ("series", item is IHasSeries series && !string.IsNullOrWhiteSpace(series.SeriesName) ? series.SeriesName : item.Name), ("episode", item.Name));
    }

    private bool IsIncluded(Jellyfin.Database.Implementations.Entities.User user) => _includedUsers is null || _includedUsers.Contains(user.Id);

    private static HashSet<Guid>? ParseIncludedUsers(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
        {
            return null;
        }

        var users = raw.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries)
            .Select(Guid.Parse)
            .ToHashSet();
        return users;
    }

    private readonly record struct ResolutionObservation(string CanonicalPath, string Reason, bool IsHot);
}
