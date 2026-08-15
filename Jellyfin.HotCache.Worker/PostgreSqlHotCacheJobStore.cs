using Npgsql;

namespace Jellyfin.HotCache.Worker;

/// <summary>PostgreSQL implementation using row locks and expiring worker leases.</summary>
public sealed class PostgreSqlHotCacheJobStore(NpgsqlDataSource dataSource) : IHotCacheJobStore
{
    public async Task<HotCacheJob?> ClaimAsync(string workerId, TimeSpan leaseDuration, CancellationToken ct)
    {
        const string sql = """
            WITH next AS (SELECT id FROM hot_cache_jobs WHERE state = 'pending' OR (state = 'running' AND lease_expires_at < now()) ORDER BY priority DESC, created_at, id FOR UPDATE SKIP LOCKED LIMIT 1)
            UPDATE hot_cache_jobs j SET state='running', lease_owner=@owner, lease_expires_at=now() + @lease, attempts=j.attempts+1, updated_at=now() FROM next WHERE j.id=next.id
            RETURNING j.id,j.kind,j.canonical_path,j.hot_path,j.source_length,j.source_modified_utc,j.priority,j.is_active,j.is_pinned,j.is_copying,j.last_access_utc,j.attempts,j.item_id;
            """;
        await using var command = dataSource.CreateCommand(sql); command.Parameters.AddWithValue("owner", workerId); command.Parameters.AddWithValue("lease", leaseDuration);
        await using var reader = await command.ExecuteReaderAsync(ct).ConfigureAwait(false); return await reader.ReadAsync(ct).ConfigureAwait(false) ? Read(reader) : null;
    }
    public Task<bool> RenewAsync(Guid id, string owner, TimeSpan lease, CancellationToken ct) => ExecuteOwnedAsync("UPDATE hot_cache_jobs SET lease_expires_at=now()+@lease,updated_at=now() WHERE id=@id AND lease_owner=@owner AND lease_expires_at>now()", id, owner, ct, ("lease", lease));
    public Task ProgressAsync(Guid id, string owner, long bytes, CancellationToken ct) => ExecuteOwnedAsync("UPDATE hot_cache_jobs SET bytes_copied=@bytes,updated_at=now() WHERE id=@id AND lease_owner=@owner AND lease_expires_at>now()", id, owner, ct, ("bytes", bytes));
    public Task CompleteAsync(Guid id, string owner, string? hotPath, string backend, CancellationToken ct) => ExecuteOwnedAsync("UPDATE hot_cache_jobs SET state='completed',is_copying=false,hot_path=CASE WHEN kind='eviction' THEN NULL ELSE COALESCE(@hotPath,hot_path) END,backend=CASE WHEN kind='eviction' THEN NULL ELSE @backend END,lease_owner=NULL,lease_expires_at=NULL,updated_at=now() WHERE id=@id AND lease_owner=@owner AND lease_expires_at>now()", id, owner, ct, ("hotPath", (object?)hotPath ?? DBNull.Value), ("backend", backend));
    public Task FailAsync(Guid id, string owner, string error, CancellationToken ct) => ExecuteOwnedAsync("UPDATE hot_cache_jobs SET state=CASE WHEN attempts >= max_attempts THEN 'failed' ELSE 'pending' END,is_copying=false,last_error=@error,lease_owner=NULL,lease_expires_at=NULL,updated_at=now() WHERE id=@id AND lease_owner=@owner", id, owner, ct, ("error", error));
    public async Task<HotCacheJob?> ClaimEvictionAsync(string workerId, TimeSpan leaseDuration, CancellationToken ct)
    { const string sql = "WITH next AS (SELECT id FROM hot_cache_jobs j WHERE state='completed' AND hot_path IS NOT NULL AND NOT is_active AND NOT is_pinned AND NOT is_copying AND priority <= 0 AND NOT EXISTS(SELECT 1 FROM hot_cache_playback_leases l WHERE l.item_id=j.item_id AND l.expires_at_utc>now()) ORDER BY last_access_utc,id FOR UPDATE SKIP LOCKED LIMIT 1) UPDATE hot_cache_jobs j SET kind='eviction',state='running',is_copying=true,lease_owner=@owner,lease_expires_at=now()+@lease,attempts=j.attempts+1,updated_at=now() FROM next WHERE j.id=next.id RETURNING j.id,j.kind,j.canonical_path,j.hot_path,j.source_length,j.source_modified_utc,j.priority,j.is_active,j.is_pinned,j.is_copying,j.last_access_utc,j.attempts,j.item_id;"; await using var cmd=dataSource.CreateCommand(sql); cmd.Parameters.AddWithValue("owner",workerId);cmd.Parameters.AddWithValue("lease",leaseDuration);await using var r=await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);return await r.ReadAsync(ct).ConfigureAwait(false)?Read(r):null; }
    public Task<bool> CanEvictAsync(Guid id, string owner, CancellationToken ct) => ExecuteOwnedAsync("UPDATE hot_cache_jobs j SET updated_at=now() WHERE id=@id AND state='running' AND lease_owner=@owner AND lease_expires_at>now() AND NOT is_active AND NOT is_pinned AND priority<=0 AND NOT EXISTS(SELECT 1 FROM hot_cache_playback_leases l WHERE l.item_id=j.item_id AND l.expires_at_utc>now())", id, owner, ct);
    public Task DeferEvictionAsync(Guid id, string owner, CancellationToken ct) => ExecuteOwnedAsync("UPDATE hot_cache_jobs SET kind='promotion',state='completed',is_copying=false,lease_owner=NULL,lease_expires_at=NULL,updated_at=now() WHERE id=@id AND kind='eviction' AND lease_owner=@owner AND lease_expires_at>now()", id, owner, ct);
    public async Task<HotCacheQueueSnapshot> SnapshotAsync(CancellationToken ct)
    { const string sql="WITH retained_events AS (DELETE FROM hot_cache_events WHERE created_at < now() - interval '30 days' RETURNING id), retained_history AS (DELETE FROM hot_cache_admin_history WHERE created_at < now() - interval '30 days' RETURNING id) SELECT count(*) FILTER (WHERE state IN ('pending','running')), COALESCE(EXTRACT(EPOCH FROM now()-min(created_at) FILTER (WHERE state IN ('pending','running'))),0), COALESCE(EXTRACT(EPOCH FROM now()-min(updated_at) FILTER (WHERE state='running' AND lease_expires_at>now())),0), pg_total_relation_size('hot_cache_events') + pg_total_relation_size('hot_cache_jobs') + pg_total_relation_size('hot_cache_admin_history') + pg_total_relation_size('hot_cache_interests') + pg_total_relation_size('hot_cache_playback_leases') + pg_total_relation_size('hot_cache_backend_observations'), (SELECT count(*) FROM hot_cache_events), (SELECT count(*) FROM hot_cache_events WHERE kind='validate-or-repair' AND detail='hot-miss' AND created_at > now() - interval '15 minutes'), (SELECT count(*) FROM hot_cache_events WHERE kind='validate-or-repair' AND detail <> 'hot-miss' AND created_at > now() - interval '15 minutes') FROM hot_cache_jobs"; await using var cmd=dataSource.CreateCommand(sql); await using var r=await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false); await r.ReadAsync(ct).ConfigureAwait(false); return new(r.GetInt64(0), TimeSpan.FromSeconds(r.GetDouble(1)), TimeSpan.FromSeconds(r.GetDouble(2)), r.GetInt64(3), r.GetInt64(4), r.GetInt64(5), r.GetInt64(6)); }
    public async Task EventAsync(Guid id, string kind, string detail, CancellationToken ct) { await using var cmd = dataSource.CreateCommand("INSERT INTO hot_cache_events(job_id,kind,detail) VALUES(@id,@kind,@detail)"); cmd.Parameters.AddWithValue("id", id); cmd.Parameters.AddWithValue("kind", kind); cmd.Parameters.AddWithValue("detail", detail); await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false); }
    private async Task<bool> ExecuteOwnedAsync(string sql, Guid id, string owner, CancellationToken ct, params (string Name, object Value)[] values) { await using var cmd = dataSource.CreateCommand(sql); cmd.Parameters.AddWithValue("id", id); cmd.Parameters.AddWithValue("owner", owner); foreach (var value in values) cmd.Parameters.AddWithValue(value.Name, value.Value); return await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false) == 1; }
    private static HotCacheJob Read(NpgsqlDataReader r) => new(r.GetGuid(0), Enum.Parse<HotCacheJobKind>(r.GetString(1), true), r.GetString(2), r.IsDBNull(3) ? null : r.GetString(3), r.GetInt64(4), r.GetDateTime(5), r.GetInt32(6), r.GetBoolean(7), r.GetBoolean(8), r.GetBoolean(9), r.GetDateTime(10), r.GetInt32(11), r.IsDBNull(12) ? null : r.GetGuid(12));
    public async Task<HotCacheWorkerSettings> GetSettingsAsync(CancellationToken ct)
    {
        await using var cmd = dataSource.CreateCommand("SELECT backend,paused,high_watermark,low_watermark,reserve_free_bytes FROM hot_cache_settings WHERE id=true");
        await using var reader = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
        return await reader.ReadAsync(ct).ConfigureAwait(false)
            ? new HotCacheWorkerSettings(reader.GetString(0), reader.GetBoolean(1), reader.GetDouble(2), reader.GetDouble(3), reader.GetInt64(4))
            : new HotCacheWorkerSettings("unraid-temp", false, .90, .75);
    }
    public async Task ObserveBackendAsync(string backend, bool mounted, bool healthy, long totalBytes, long availableBytes, CancellationToken ct)
    {
        await using var previous = dataSource.CreateCommand("SELECT mounted,healthy FROM hot_cache_backend_observations WHERE backend=@backend");
        previous.Parameters.AddWithValue("backend", backend);
        await using var reader = await previous.ExecuteReaderAsync(ct).ConfigureAwait(false);
        var changed = !await reader.ReadAsync(ct).ConfigureAwait(false)
            || reader.GetBoolean(0) != mounted
            || reader.GetBoolean(1) != healthy;

        const string sql = "INSERT INTO hot_cache_backend_observations(backend,mounted,healthy,total_bytes,used_bytes,available_bytes,observed_at) VALUES(@backend,@mounted,@healthy,@total,@used,@available,now()) ON CONFLICT(backend) DO UPDATE SET mounted=excluded.mounted,healthy=excluded.healthy,total_bytes=excluded.total_bytes,used_bytes=excluded.used_bytes,available_bytes=excluded.available_bytes,observed_at=excluded.observed_at";
        await using var cmd = dataSource.CreateCommand(sql);
        cmd.Parameters.AddWithValue("backend", backend); cmd.Parameters.AddWithValue("mounted", mounted); cmd.Parameters.AddWithValue("healthy", healthy); cmd.Parameters.AddWithValue("total", totalBytes); cmd.Parameters.AddWithValue("used", Math.Max(0, totalBytes - availableBytes)); cmd.Parameters.AddWithValue("available", availableBytes);
        await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
        if (!changed)
        {
            return;
        }

        await using var history = dataSource.CreateCommand("INSERT INTO hot_cache_admin_history(kind,detail) VALUES('backend',@detail)");
        history.Parameters.AddWithValue("detail", $"backend={backend}; mounted={mounted}; healthy={healthy}");
        await history.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
    }
}
