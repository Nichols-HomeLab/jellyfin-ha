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

    public async Task<HotCacheAdministrationSnapshot> GetSnapshotAsync(string? historyKind, CancellationToken cancellationToken)
    {
        if (historyKind is not null && Array.IndexOf(ValidHistoryKinds, historyKind) < 0)
        {
            throw new ArgumentException("Unknown history kind.", nameof(historyKind));
        }

        var settings = await ReadSettingsAsync(cancellationToken).ConfigureAwait(false);
        return new HotCacheAdministrationSnapshot(settings, await ReadBackendsAsync(cancellationToken).ConfigureAwait(false), await ReadQueueAsync(cancellationToken).ConfigureAwait(false), await ReadInventoryAsync(settings.Backend, cancellationToken).ConfigureAwait(false), await ReadHistoryAsync(historyKind, cancellationToken).ConfigureAwait(false));
    }

    public async Task UpdateSettingsAsync(HotCacheSettings settings, CancellationToken cancellationToken)
    {
        Validate(settings);
        const string sql = "UPDATE hot_cache_settings SET backend=@backend,paused=@paused,high_watermark=@high,low_watermark=@low,updated_at=now() WHERE id=true";
        await using var command = dataSource.CreateCommand(sql);
        command.Parameters.AddWithValue("backend", settings.Backend);
        command.Parameters.AddWithValue("paused", settings.Paused);
        command.Parameters.AddWithValue("high", settings.HighWatermark);
        command.Parameters.AddWithValue("low", settings.LowWatermark);
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        await HistoryAsync("settings", $"backend={settings.Backend}; paused={settings.Paused}; high={settings.HighWatermark}; low={settings.LowWatermark}", cancellationToken).ConfigureAwait(false);
    }

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

        if (action.Kind is not "reconcile" && action.ItemId is null)
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

        var kind = action.Kind == "promote" ? "promotion" : "eviction";
        const string sql = "UPDATE hot_cache_jobs SET state='pending', attempts=0, last_error=NULL, updated_at=now() WHERE (@id IS NULL OR id=@id) AND (@kind IS NULL OR kind=@kind)";
        await using var command = dataSource.CreateCommand(sql);
        command.Parameters.AddWithValue("id", (object?)action.ItemId ?? DBNull.Value);
        command.Parameters.AddWithValue("kind", action.Kind == "retry" ? DBNull.Value : kind);
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        await HistoryAsync(action.Kind == "promote" ? "promoted" : action.Kind, action.ItemId?.ToString() ?? "bulk", cancellationToken).ConfigureAwait(false);
    }

    private async Task<HotCacheSettings> ReadSettingsAsync(CancellationToken ct)
    {
        await using var command = dataSource.CreateCommand("SELECT backend,paused,high_watermark,low_watermark FROM hot_cache_settings WHERE id=true");
        await using var reader = await command.ExecuteReaderAsync(ct).ConfigureAwait(false);
        return await reader.ReadAsync(ct).ConfigureAwait(false) ? new(reader.GetString(0), reader.GetBoolean(1), reader.GetDouble(2), reader.GetDouble(3)) : new("unraid-temp", false, .90, .75);
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
        const string sql = "SELECT state,COUNT(*),COALESCE(SUM(source_length),0) FROM hot_cache_jobs GROUP BY state ORDER BY state";
        var results = new List<HotCacheQueueSummary>();
        await using var command = dataSource.CreateCommand(sql);
        await using var reader = await command.ExecuteReaderAsync(ct).ConfigureAwait(false);
        while (await reader.ReadAsync(ct).ConfigureAwait(false))
        {
            results.Add(new(reader.GetString(0), reader.GetInt64(1), reader.GetInt64(2)));
        }

        return results;
    }

    private async Task<IReadOnlyList<HotCacheInventoryItem>> ReadInventoryAsync(string backend, CancellationToken ct)
    {
        const string sql = "SELECT j.id,COALESCE(i.reason,''),COALESCE(i.users,0),j.priority,j.source_length,@backend,j.created_at,j.updated_at,j.state FROM hot_cache_jobs j LEFT JOIN (SELECT item_id,STRING_AGG(DISTINCT reason,',' ) reason,COUNT(DISTINCT user_id) users FROM hot_cache_interests WHERE expires_at_utc > now() GROUP BY item_id) i ON i.item_id=j.item_id ORDER BY j.updated_at DESC LIMIT 500";
        var results = new List<HotCacheInventoryItem>();
        await using var command = dataSource.CreateCommand(sql);
        command.Parameters.AddWithValue("backend", backend);
        await using var reader = await command.ExecuteReaderAsync(ct).ConfigureAwait(false);
        while (await reader.ReadAsync(ct).ConfigureAwait(false))
        {
            results.Add(new(reader.GetGuid(0), "Uncatalogued", reader.GetGuid(0).ToString(), reader.GetString(1), checked((int)reader.GetInt64(2)), reader.GetInt32(3), reader.GetInt64(4), reader.GetString(5), reader.GetDateTime(6), reader.GetDateTime(7), reader.GetString(8)));
        }

        return results;
    }

    private async Task<IReadOnlyList<HotCacheHistoryEntry>> ReadHistoryAsync(string? kind, CancellationToken ct)
    {
        const string sql = "SELECT id,kind,detail,created_at FROM hot_cache_admin_history WHERE (@kind IS NULL OR kind=@kind) ORDER BY id DESC LIMIT 500";
        var results = new List<HotCacheHistoryEntry>();
        await using var command = dataSource.CreateCommand(sql);
        command.Parameters.AddWithValue("kind", (object?)kind ?? DBNull.Value);
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

        if (settings.LowWatermark <= 0 || settings.HighWatermark >= 1 || settings.LowWatermark >= settings.HighWatermark)
        {
            throw new ArgumentException("Watermarks must be between zero and one, with low below high.");
        }
    }
}
