using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using MediaBrowser.Controller.Library;
using Npgsql;

namespace Jellyfin.Server.Implementations.HotCache;

/// <summary>PostgreSQL implementation of the administrator hot-cache view and commands.</summary>
public sealed class PostgreSqlHotCacheAdministration(NpgsqlDataSource dataSource, IHotCacheCoordinator coordinator) : IHotCacheAdministration
{
    private static readonly string[] ValidHistoryKinds = ["copied", "evicted", "failed", "settings", "backend", "promoted", "retry", "reconcile"];

    /// <summary>Gets the current shared settings, backend observations, queue, inventory, and history.</summary>
    /// <param name="historyKind">Optional history category filter.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>The administration snapshot.</returns>
    public async Task<HotCacheAdministrationSnapshot> GetSnapshotAsync(string? historyKind, CancellationToken cancellationToken)
    {
        if (historyKind is not null && Array.IndexOf(ValidHistoryKinds, historyKind) < 0)
        {
            throw new ArgumentException("Unknown history kind.", nameof(historyKind));
        }

        var settings = await ReadSettingsAsync(cancellationToken).ConfigureAwait(false);
        return new HotCacheAdministrationSnapshot(settings, await ReadBackendsAsync(cancellationToken).ConfigureAwait(false), await ReadQueueAsync(cancellationToken).ConfigureAwait(false), await ReadInventoryAsync(cancellationToken).ConfigureAwait(false), await ReadHistoryAsync(historyKind, cancellationToken).ConfigureAwait(false));
    }

    /// <summary>Updates the shared worker controls and validated watermarks.</summary>
    /// <param name="settings">The replacement settings.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>A task representing the update.</returns>
    public async Task UpdateSettingsAsync(HotCacheSettings settings, CancellationToken cancellationToken)
    {
        Validate(settings);
        const string sql = "UPDATE hot_cache_settings SET backend=@backend,paused=@paused,high_watermark=@high,low_watermark=@low,max_lookahead=@lookahead,reserve_free_bytes=@reserve,updated_at=now() WHERE id=true";
        await using var command = dataSource.CreateCommand(sql);
        command.Parameters.AddWithValue("backend", settings.Backend);
        command.Parameters.AddWithValue("paused", settings.Paused);
        command.Parameters.AddWithValue("high", settings.HighWatermark);
        command.Parameters.AddWithValue("low", settings.LowWatermark);
        command.Parameters.AddWithValue("lookahead", settings.MaxLookahead);
        command.Parameters.AddWithValue("reserve", settings.ReserveFreeBytes);
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        await HistoryAsync("settings", $"backend={settings.Backend}; paused={settings.Paused}; high={settings.HighWatermark}; low={settings.LowWatermark}; switch=former-backend-drains", cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Queues a safe action against existing hot-cache inventory.</summary>
    /// <param name="action">The requested action.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>A task representing the action request.</returns>
    public async Task QueueActionAsync(HotCacheAction action, CancellationToken cancellationToken)
    {
        if (action.Kind is not ("promote" or "evict" or "retry" or "reconcile"))
        {
            throw new ArgumentException("Unknown action kind.", nameof(action));
        }

        if (action.Kind == "evict" && action.ItemId is null && !action.ConfirmBulkEviction)
        {
            throw new ArgumentException("Bulk eviction requires confirmation.", nameof(action));
        }

        if (action.Kind is not "reconcile" && action.Kind is not "evict" && action.ItemId is null)
        {
            throw new ArgumentException("An inventory item is required.", nameof(action));
        }

        if (action.Kind == "reconcile")
        {
            await coordinator.ReconcileAsync(cancellationToken).ConfigureAwait(false);
            await HistoryAsync("reconcile", "requested", cancellationToken).ConfigureAwait(false);
            return;
        }

        if (action.ItemId is not null)
        {
            const string exists = "SELECT EXISTS(SELECT 1 FROM hot_cache_jobs WHERE id=@id)";
            await using var check = dataSource.CreateCommand(exists);
            check.Parameters.AddWithValue("id", action.ItemId.Value);
            if (!(bool)(await check.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false) ?? false))
            {
                throw new KeyNotFoundException("Inventory item was not found.");
            }
        }

        const string sql = "UPDATE hot_cache_jobs j SET kind=CASE WHEN @action='promote' THEN 'promotion' WHEN @action='evict' THEN 'eviction' ELSE j.kind END,state='pending',attempts=0,last_error=NULL,lease_owner=NULL,lease_expires_at=NULL,updated_at=now() WHERE (@id IS NULL OR j.id=@id) AND (@action <> 'promote' OR j.state <> 'running') AND (@action <> 'retry' OR j.state='failed') AND (@action <> 'evict' OR (j.state='completed' AND j.hot_path IS NOT NULL AND NOT j.is_active AND NOT j.is_pinned AND j.priority <= 0 AND NOT EXISTS(SELECT 1 FROM hot_cache_playback_leases l WHERE l.item_id=j.item_id AND l.expires_at_utc>now())))";
        await using var command = dataSource.CreateCommand(sql);
        command.Parameters.AddWithValue("id", (object?)action.ItemId ?? DBNull.Value);
        command.Parameters.AddWithValue("action", action.Kind);
        var updated = await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        if (action.ItemId is not null && updated == 0)
        {
            throw new ArgumentException("The requested action is not safe for the item.", nameof(action));
        }

        await HistoryAsync(action.Kind == "promote" ? "promoted" : action.Kind == "evict" ? "evicted" : action.Kind, action.ItemId?.ToString() ?? "bulk", cancellationToken).ConfigureAwait(false);
    }

    private async Task<HotCacheSettings> ReadSettingsAsync(CancellationToken ct)
    {
        await using var command = dataSource.CreateCommand("SELECT backend,paused,high_watermark,low_watermark,max_lookahead,reserve_free_bytes FROM hot_cache_settings WHERE id=true");
        await using var reader = await command.ExecuteReaderAsync(ct).ConfigureAwait(false);
        return await reader.ReadAsync(ct).ConfigureAwait(false) ? new(reader.GetString(0), reader.GetBoolean(1), reader.GetDouble(2), reader.GetDouble(3), reader.GetInt32(4), reader.GetInt64(5)) : new("unraid-temp", false, .90, .75);
    }

    private async Task<IReadOnlyList<HotCacheBackendStatus>> ReadBackendsAsync(CancellationToken ct)
    {
        const string sql = "SELECT backend,mounted,healthy,total_bytes,used_bytes,available_bytes,observed_at FROM hot_cache_backend_observations ORDER BY backend";
        var results = new List<HotCacheBackendStatus>();
        await using var command = dataSource.CreateCommand(sql);
        await using var reader = await command.ExecuteReaderAsync(ct).ConfigureAwait(false);
        while (await reader.ReadAsync(ct).ConfigureAwait(false))
        {
            var at = reader.GetDateTime(6);
            results.Add(new(reader.GetString(0), reader.GetBoolean(1), reader.GetBoolean(2), at < DateTime.UtcNow.AddMinutes(-2), reader.GetInt64(3), reader.GetInt64(4), reader.GetInt64(5), at));
        }

        return results;
    }

    private async Task<IReadOnlyList<HotCacheQueueSummary>> ReadQueueAsync(CancellationToken ct)
    {
        const string sql = "WITH states(state) AS (VALUES ('queued'),('copying'),('evicting'),('copied'),('evicted'),('failed')), totals AS (SELECT CASE WHEN state='pending' THEN 'queued' WHEN state='running' AND kind='promotion' THEN 'copying' WHEN state='running' AND kind='eviction' THEN 'evicting' WHEN state='completed' AND kind='promotion' THEN 'copied' WHEN state='completed' AND kind='eviction' THEN 'evicted' ELSE 'failed' END AS state,COUNT(*) AS count,COALESCE(SUM(source_length),0) AS bytes FROM hot_cache_jobs GROUP BY 1) SELECT states.state,COALESCE(totals.count,0),COALESCE(totals.bytes,0) FROM states LEFT JOIN totals ON totals.state=states.state ORDER BY states.state";
        var results = new List<HotCacheQueueSummary>();
        await using var command = dataSource.CreateCommand(sql);
        await using var reader = await command.ExecuteReaderAsync(ct).ConfigureAwait(false);
        while (await reader.ReadAsync(ct).ConfigureAwait(false))
        {
            results.Add(new(reader.GetString(0), reader.GetInt64(1), reader.GetInt64(2)));
        }

        return results;
    }

    private async Task<IReadOnlyList<HotCacheInventoryItem>> ReadInventoryAsync(CancellationToken ct)
    {
        const string sql = "SELECT j.id,COALESCE(j.series_name,'Uncatalogued'),COALESCE(j.episode_name,j.id::text),COALESCE(i.reason,''),COALESCE(i.users,0),j.priority,j.source_length,COALESCE(j.backend,''),j.created_at,j.updated_at,CASE WHEN j.state='pending' THEN 'queued' WHEN j.state='running' AND j.kind='promotion' THEN 'copying' WHEN j.state='running' AND j.kind='eviction' THEN 'evicting' WHEN j.state='completed' AND j.kind='promotion' THEN 'copied' WHEN j.state='completed' AND j.kind='eviction' THEN 'evicted' ELSE 'failed' END FROM hot_cache_jobs j LEFT JOIN (SELECT item_id,STRING_AGG(DISTINCT reason,',' ) reason,COUNT(DISTINCT user_id) users FROM hot_cache_interests WHERE expires_at_utc > now() GROUP BY item_id) i ON i.item_id=j.item_id ORDER BY j.updated_at DESC,j.id LIMIT 500";
        var results = new List<HotCacheInventoryItem>();
        await using var command = dataSource.CreateCommand(sql);
        await using var reader = await command.ExecuteReaderAsync(ct).ConfigureAwait(false);
        while (await reader.ReadAsync(ct).ConfigureAwait(false))
        {
            results.Add(new(reader.GetGuid(0), reader.GetString(1), reader.GetString(2), reader.GetString(3), checked((int)reader.GetInt64(4)), reader.GetInt32(5), reader.GetInt64(6), reader.GetString(7), reader.GetDateTime(8), reader.GetDateTime(9), reader.GetString(10)));
        }

        return results;
    }

    private async Task<IReadOnlyList<HotCacheHistoryEntry>> ReadHistoryAsync(string? kind, CancellationToken ct)
    {
        const string sql = "WITH history AS (SELECT id,kind,detail,created_at FROM hot_cache_admin_history UNION ALL SELECT -id,CASE WHEN kind IN ('published','already-published') THEN 'copied' WHEN kind='evicted' THEN 'evicted' WHEN kind='failed' THEN 'failed' ELSE kind END,detail,created_at FROM hot_cache_events WHERE kind IN ('published','already-published','evicted','failed')) SELECT id,kind,detail,created_at FROM history WHERE (@kind IS NULL OR kind=@kind) ORDER BY created_at DESC,id DESC LIMIT 500";
        var results = new List<HotCacheHistoryEntry>();
        await using var command = dataSource.CreateCommand(sql);
        // PostgreSQL cannot infer a type for a NULL-only predicate parameter.
        // The unfiltered administrator view deliberately passes null here.
        command.Parameters.Add("kind", NpgsqlTypes.NpgsqlDbType.Text).Value = (object?)kind ?? DBNull.Value;
        await using var reader = await command.ExecuteReaderAsync(ct).ConfigureAwait(false);
        while (await reader.ReadAsync(ct).ConfigureAwait(false))
        {
            results.Add(new(reader.GetInt64(0), reader.GetString(1), reader.GetString(2), reader.GetDateTime(3)));
        }

        return results;
    }

    private async Task HistoryAsync(string kind, string detail, CancellationToken ct)
    {
        await using var command = dataSource.CreateCommand("INSERT INTO hot_cache_admin_history(kind,detail) VALUES(@kind,@detail)");
        command.Parameters.AddWithValue("kind", kind);
        command.Parameters.AddWithValue("detail", detail);
        await command.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
    }

    private static void Validate(HotCacheSettings settings)
    {
        if (settings.Backend is not ("unraid-temp" or "cephfs"))
        {
            throw new ArgumentException("Unknown backend.");
        }

        if (settings.LowWatermark <= 0 || settings.HighWatermark >= 1 || settings.LowWatermark >= settings.HighWatermark || settings.MaxLookahead < 0 || settings.ReserveFreeBytes < 0)
        {
            throw new ArgumentException("Watermarks must be between zero and one, with low below high.");
        }
    }
}
