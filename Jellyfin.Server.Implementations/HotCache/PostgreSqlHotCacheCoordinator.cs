using System;
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
    private readonly Channel<ResolutionObservation> _observations = Channel.CreateBounded<ResolutionObservation>(new BoundedChannelOptions(1024) { FullMode = BoundedChannelFullMode.DropWrite });

    /// <summary>
    /// Initializes a new instance of the <see cref="PostgreSqlHotCacheCoordinator"/> class.
    /// </summary>
    /// <param name="dataSource">The shared PostgreSQL data source.</param>
    /// <param name="userManager">The Jellyfin user source.</param>
    /// <param name="libraryManager">The Jellyfin library query source.</param>
    public PostgreSqlHotCacheCoordinator(NpgsqlDataSource dataSource, IUserManager userManager, ILibraryManager libraryManager)
    {
        _dataSource = dataSource;
        _userManager = userManager;
        _libraryManager = libraryManager;
    }

    /// <inheritdoc />
    public async Task RecordPlaybackAsync(PlaybackProgressEventArgs playback, HotCachePlaybackEvent lifecycle, CancellationToken cancellationToken)
    {
        if (playback.Item is null || playback.Users is null || playback.Users.Count == 0)
        {
            return;
        }

        await using var connection = await _dataSource.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        if (lifecycle == HotCachePlaybackEvent.Stopped)
        {
            await ExecuteAsync(connection, transaction, "DELETE FROM hot_cache_playback_leases WHERE play_session_id=@session", cancellationToken, ("session", playback.PlaySessionId ?? string.Empty)).ConfigureAwait(false);
        }
        else
        {
            await ExecuteAsync(connection, transaction, "INSERT INTO hot_cache_playback_leases(play_session_id,item_id,expires_at_utc) VALUES(@session,@item,now()+@lease) ON CONFLICT(play_session_id) DO UPDATE SET item_id=excluded.item_id,expires_at_utc=excluded.expires_at_utc,updated_at_utc=now()", cancellationToken, ("session", playback.PlaySessionId ?? string.Empty), ("item", playback.Item.Id), ("lease", PlaybackLeaseLifetime)).ConfigureAwait(false);
            foreach (var user in playback.Users)
            {
                await ExecuteAsync(connection, transaction, "INSERT INTO hot_cache_interests(item_id,user_id,reason,priority,expires_at_utc) VALUES(@item,@user,'playback',100,now()+@expiry) ON CONFLICT(item_id,user_id,reason) DO UPDATE SET priority=excluded.priority,expires_at_utc=excluded.expires_at_utc,last_observed_utc=now()", cancellationToken, ("item", playback.Item.Id), ("user", user.Id), ("expiry", PlaybackInterestLifetime)).ConfigureAwait(false);
                await UpsertJobAsync(connection, transaction, playback.Item, 100, cancellationToken).ConfigureAwait(false);
            }
        }

        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
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

        await ExecuteAsync(connection, transaction, "DELETE FROM hot_cache_interests WHERE expires_at_utc <= now(); DELETE FROM hot_cache_playback_leases WHERE expires_at_utc <= now(); UPDATE hot_cache_jobs j SET priority=COALESCE((SELECT MAX(interest.priority) FROM hot_cache_interests interest WHERE interest.item_id=j.item_id AND interest.expires_at_utc>now()),0),is_active=EXISTS(SELECT 1 FROM hot_cache_playback_leases lease WHERE lease.item_id=j.item_id AND lease.expires_at_utc>now()),updated_at=now() WHERE j.state IN ('pending','running');", cancellationToken).ConfigureAwait(false);
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public void ObserveResolution(in PlaybackPathRequest request, in PlaybackPathResolution resolution)
    {
        _observations.Writer.TryWrite(new ResolutionObservation(request.CanonicalPath, resolution.Reason, resolution.IsHot));
    }

    /// <summary>Creates the additive schema shared with the worker queue from issue 70.</summary>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>A task that completes when the schema is available.</returns>
    public async Task EnsureMigratedAsync(CancellationToken cancellationToken)
    {
        const string sql = """
            CREATE TABLE IF NOT EXISTS hot_cache_jobs (id uuid PRIMARY KEY, kind text NOT NULL, state text NOT NULL DEFAULT 'pending', canonical_path text NOT NULL, hot_path text, source_length bigint NOT NULL DEFAULT 0, source_modified_utc timestamptz NOT NULL DEFAULT now(), priority integer NOT NULL DEFAULT 0, is_active boolean NOT NULL DEFAULT false, is_pinned boolean NOT NULL DEFAULT false, is_copying boolean NOT NULL DEFAULT false, last_access_utc timestamptz NOT NULL DEFAULT now(), bytes_copied bigint NOT NULL DEFAULT 0, attempts integer NOT NULL DEFAULT 0, max_attempts integer NOT NULL DEFAULT 3, last_error varchar(512), lease_owner text, lease_expires_at timestamptz, created_at timestamptz NOT NULL DEFAULT now(), updated_at timestamptz NOT NULL DEFAULT now());
            CREATE TABLE IF NOT EXISTS hot_cache_events (id bigint GENERATED ALWAYS AS IDENTITY PRIMARY KEY, job_id uuid NOT NULL REFERENCES hot_cache_jobs(id), kind text NOT NULL, detail varchar(512) NOT NULL, created_at timestamptz NOT NULL DEFAULT now());
            CREATE TABLE IF NOT EXISTS hot_cache_interests (item_id uuid NOT NULL, user_id uuid NOT NULL, reason text NOT NULL, priority integer NOT NULL, first_observed_utc timestamptz NOT NULL DEFAULT now(), last_observed_utc timestamptz NOT NULL DEFAULT now(), expires_at_utc timestamptz NOT NULL, PRIMARY KEY(item_id,user_id,reason));
            CREATE TABLE IF NOT EXISTS hot_cache_playback_leases (play_session_id text PRIMARY KEY, item_id uuid NOT NULL, expires_at_utc timestamptz NOT NULL, updated_at_utc timestamptz NOT NULL DEFAULT now());
            CREATE INDEX IF NOT EXISTS hot_cache_playback_leases_item_expiry_idx ON hot_cache_playback_leases(item_id,expires_at_utc);
            ALTER TABLE hot_cache_jobs ADD COLUMN IF NOT EXISTS item_id uuid;
            ALTER TABLE hot_cache_jobs ADD COLUMN IF NOT EXISTS source_mtime_utc timestamptz;
            CREATE INDEX IF NOT EXISTS hot_cache_jobs_item_idx ON hot_cache_jobs(item_id);
            """;
        await using var command = _dataSource.CreateCommand(sql);
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Persists bounded resolver observations without adding a database round trip to playback.</summary>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>A task that completes when the pending observations are drained.</returns>
    public async Task DrainObservationsAsync(CancellationToken cancellationToken)
    {
        while (_observations.Reader.TryRead(out var observation))
        {
            await using var command = _dataSource.CreateCommand("INSERT INTO hot_cache_events(job_id,kind,detail) SELECT id,@kind,@detail FROM hot_cache_jobs WHERE canonical_path=@path ORDER BY updated_at DESC LIMIT 1");
            command.Parameters.AddWithValue("kind", observation.IsHot ? "playback-hit" : "validate-or-repair");
            command.Parameters.AddWithValue("detail", observation.Reason);
            command.Parameters.AddWithValue("path", observation.CanonicalPath);
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

        foreach (var item in recent)
        {
            await RecordCandidateAsync(connection, transaction, item, user.Id, "recent-series", 10, cancellationToken).ConfigureAwait(false);
            if (item is MediaBrowser.Controller.Entities.TV.Episode episode)
            {
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
        return ExecuteAsync(connection, transaction, "INSERT INTO hot_cache_jobs(id,kind,state,item_id,canonical_path,source_length,source_modified_utc,priority) VALUES(@id,'promotion','pending',@item,@path,@length,@mtime,@priority) ON CONFLICT(item_id) DO UPDATE SET priority=GREATEST(hot_cache_jobs.priority,excluded.priority),source_length=excluded.source_length,source_modified_utc=excluded.source_modified_utc,updated_at=now()", cancellationToken, ("id", Guid.NewGuid()), ("item", item.Id), ("path", item.Path), ("length", source.Exists ? source.Length : 0L), ("mtime", source.Exists ? source.LastWriteTimeUtc : DateTime.UnixEpoch), ("priority", priority));
    }

    private readonly record struct ResolutionObservation(string CanonicalPath, string Reason, bool IsHot);
}
