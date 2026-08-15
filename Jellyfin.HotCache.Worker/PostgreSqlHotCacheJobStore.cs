using Npgsql;

namespace Jellyfin.HotCache.Worker;

/// <summary>PostgreSQL implementation using row locks and expiring worker leases.</summary>
public sealed class PostgreSqlHotCacheJobStore(NpgsqlDataSource dataSource) : IHotCacheJobStore
{
    public async Task<HotCacheJob?> ClaimAsync(string workerId, TimeSpan leaseDuration, CancellationToken ct)
    {
        const string sql = """
            WITH next AS (SELECT id FROM hot_cache_jobs WHERE state = 'pending' OR (state = 'running' AND lease_expires_at < now()) ORDER BY priority DESC, created_at FOR UPDATE SKIP LOCKED LIMIT 1)
            UPDATE hot_cache_jobs j SET state='running', lease_owner=@owner, lease_expires_at=now() + @lease, attempts=j.attempts+1, updated_at=now() FROM next WHERE j.id=next.id
            RETURNING j.id,j.kind,j.canonical_path,j.hot_path,j.source_length,j.source_modified_utc,j.priority,j.is_active,j.is_pinned,j.is_copying,j.last_access_utc,j.attempts;
            """;
        await using var command = dataSource.CreateCommand(sql); command.Parameters.AddWithValue("owner", workerId); command.Parameters.AddWithValue("lease", leaseDuration);
        await using var reader = await command.ExecuteReaderAsync(ct).ConfigureAwait(false); return await reader.ReadAsync(ct).ConfigureAwait(false) ? Read(reader) : null;
    }
    public Task<bool> RenewAsync(Guid id, string owner, TimeSpan lease, CancellationToken ct) => ExecuteOwnedAsync("UPDATE hot_cache_jobs SET lease_expires_at=now()+@lease,updated_at=now() WHERE id=@id AND lease_owner=@owner AND lease_expires_at>now()", id, owner, ct, ("lease", lease));
    public Task ProgressAsync(Guid id, string owner, long bytes, CancellationToken ct) => ExecuteOwnedAsync("UPDATE hot_cache_jobs SET bytes_copied=@bytes,updated_at=now() WHERE id=@id AND lease_owner=@owner AND lease_expires_at>now()", id, owner, ct, ("bytes", bytes));
    public Task CompleteAsync(Guid id, string owner, string? hotPath, CancellationToken ct) => ExecuteOwnedAsync("UPDATE hot_cache_jobs SET state='completed',is_copying=false,hot_path=COALESCE(@hotPath,hot_path),lease_owner=NULL,lease_expires_at=NULL,updated_at=now() WHERE id=@id AND lease_owner=@owner AND lease_expires_at>now()", id, owner, ct, ("hotPath", (object?)hotPath ?? DBNull.Value));
    public Task FailAsync(Guid id, string owner, string error, CancellationToken ct) => ExecuteOwnedAsync("UPDATE hot_cache_jobs SET state=CASE WHEN attempts >= max_attempts THEN 'failed' ELSE 'pending' END,is_copying=false,last_error=@error,lease_owner=NULL,lease_expires_at=NULL,updated_at=now() WHERE id=@id AND lease_owner=@owner", id, owner, ct, ("error", error));
    public async Task<HotCacheJob?> ClaimEvictionAsync(string workerId, TimeSpan leaseDuration, CancellationToken ct)
    { const string sql = "WITH next AS (SELECT id FROM hot_cache_jobs WHERE state='completed' AND hot_path IS NOT NULL AND NOT is_active AND NOT is_pinned AND NOT is_copying AND priority <= 0 ORDER BY last_access_utc FOR UPDATE SKIP LOCKED LIMIT 1) UPDATE hot_cache_jobs j SET kind='eviction',state='running',is_copying=true,lease_owner=@owner,lease_expires_at=now()+@lease,attempts=j.attempts+1,updated_at=now() FROM next WHERE j.id=next.id RETURNING j.id,j.kind,j.canonical_path,j.hot_path,j.source_length,j.source_modified_utc,j.priority,j.is_active,j.is_pinned,j.is_copying,j.last_access_utc,j.attempts;"; await using var cmd=dataSource.CreateCommand(sql); cmd.Parameters.AddWithValue("owner",workerId);cmd.Parameters.AddWithValue("lease",leaseDuration);await using var r=await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);return await r.ReadAsync(ct).ConfigureAwait(false)?Read(r):null; }
    public Task<bool> CanEvictAsync(Guid id, string owner, CancellationToken ct) => ExecuteOwnedAsync("UPDATE hot_cache_jobs SET updated_at=now() WHERE id=@id AND state='running' AND lease_owner=@owner AND lease_expires_at>now() AND NOT is_active AND NOT is_pinned AND priority<=0", id, owner, ct);
    public async Task EventAsync(Guid id, string kind, string detail, CancellationToken ct) { await using var cmd = dataSource.CreateCommand("INSERT INTO hot_cache_events(job_id,kind,detail) VALUES(@id,@kind,@detail)"); cmd.Parameters.AddWithValue("id", id); cmd.Parameters.AddWithValue("kind", kind); cmd.Parameters.AddWithValue("detail", detail); await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false); }
    private async Task<bool> ExecuteOwnedAsync(string sql, Guid id, string owner, CancellationToken ct, params (string Name, object Value)[] values) { await using var cmd = dataSource.CreateCommand(sql); cmd.Parameters.AddWithValue("id", id); cmd.Parameters.AddWithValue("owner", owner); foreach (var value in values) cmd.Parameters.AddWithValue(value.Name, value.Value); return await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false) == 1; }
    private static HotCacheJob Read(NpgsqlDataReader r) => new(r.GetGuid(0), Enum.Parse<HotCacheJobKind>(r.GetString(1), true), r.GetString(2), r.IsDBNull(3) ? null : r.GetString(3), r.GetInt64(4), r.GetDateTime(5), r.GetInt32(6), r.GetBoolean(7), r.GetBoolean(8), r.GetBoolean(9), r.GetDateTime(10), r.GetInt32(11));
}
