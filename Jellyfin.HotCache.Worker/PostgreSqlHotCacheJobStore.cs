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
    public Task CompleteAsync(Guid id, string owner, CancellationToken ct) => ExecuteOwnedAsync("UPDATE hot_cache_jobs SET state='completed',lease_owner=NULL,lease_expires_at=NULL,updated_at=now() WHERE id=@id AND lease_owner=@owner", id, owner, ct);
    public Task FailAsync(Guid id, string owner, string error, CancellationToken ct) => ExecuteOwnedAsync("UPDATE hot_cache_jobs SET state=CASE WHEN attempts >= max_attempts THEN 'failed' ELSE 'pending' END,last_error=@error,lease_owner=NULL,lease_expires_at=NULL,updated_at=now() WHERE id=@id AND lease_owner=@owner", id, owner, ct, ("error", error));
    public async Task<IReadOnlyList<HotCacheJob>> EvictionCandidatesAsync(CancellationToken ct)
    { const string sql="SELECT id,kind,canonical_path,hot_path,source_length,source_modified_utc,priority,is_active,is_pinned,is_copying,last_access_utc,attempts FROM hot_cache_jobs WHERE kind='eviction' AND state='pending' AND NOT is_active AND NOT is_pinned AND NOT is_copying AND priority <= 0 ORDER BY last_access_utc"; await using var cmd=dataSource.CreateCommand(sql); await using var r=await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false); var result=new List<HotCacheJob>(); while(await r.ReadAsync(ct).ConfigureAwait(false)) result.Add(Read(r)); return result; }
    public async Task EventAsync(Guid id, string kind, string detail, CancellationToken ct) { await using var cmd=dataSource.CreateCommand("INSERT INTO hot_cache_events(job_id,kind,detail) VALUES(@id,@kind,@detail)"); cmd.Parameters.AddWithValue("id",id); cmd.Parameters.AddWithValue("kind",kind); cmd.Parameters.AddWithValue("detail",detail); await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false); }
    private async Task<bool> ExecuteOwnedAsync(string sql, Guid id, string owner, CancellationToken ct, params (string Name, object Value)[] values) { await using var cmd=dataSource.CreateCommand(sql); cmd.Parameters.AddWithValue("id",id); cmd.Parameters.AddWithValue("owner",owner); foreach(var value in values) cmd.Parameters.AddWithValue(value.Name,value.Value); return await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false)==1; }
    private static HotCacheJob Read(NpgsqlDataReader r) => new(r.GetGuid(0), Enum.Parse<HotCacheJobKind>(r.GetString(1), true), r.GetString(2), r.IsDBNull(3)?null:r.GetString(3), r.GetInt64(4), r.GetDateTime(5), r.GetInt32(6), r.GetBoolean(7), r.GetBoolean(8), r.GetBoolean(9), r.GetDateTime(10), r.GetInt32(11));
}
