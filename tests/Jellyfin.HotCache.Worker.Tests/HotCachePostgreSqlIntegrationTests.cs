using MediaBrowser.Controller.Library;
using Npgsql;
using Testcontainers.PostgreSql;
using Xunit;

namespace Jellyfin.HotCache.Worker.Tests;

/// <summary>Exercises the durable queue contract against an isolated PostgreSQL container.</summary>
[Trait("Category", "RequiresDocker")]
public sealed class HotCachePostgreSqlIntegrationTests : IAsyncLifetime
{
    private PostgreSqlContainer? _container;
    private string _connectionString = string.Empty;

    public async Task InitializeAsync()
    {
        _connectionString = Environment.GetEnvironmentVariable("HOT_CACHE_TEST_POSTGRES_CONNECTION_STRING") ?? string.Empty;
        if (!string.IsNullOrEmpty(_connectionString))
        {
            return;
        }

        _container = new PostgreSqlBuilder("postgres:16-alpine").Build();
        await _container.StartAsync();
        _connectionString = _container.GetConnectionString();
    }

    public Task DisposeAsync() => _container?.DisposeAsync().AsTask() ?? Task.CompletedTask;

    [Fact]
    public async Task FreshAndLegacyLedgerUpgradesAreRepeatableAndPreserveQueueContracts()
    {
        await VerifyScenarioAsync(false);
        await VerifyScenarioAsync(true);
    }

    private async Task VerifyScenarioAsync(bool legacyLedger)
    {
        var schema = "hot_cache_" + Guid.NewGuid().ToString("N");
        await using var admin = NpgsqlDataSource.Create(_connectionString);
        await using (var create = admin.CreateCommand($"CREATE SCHEMA {schema}"))
        {
            await create.ExecuteNonQueryAsync();
        }

        await using var dataSource = NpgsqlDataSource.Create(_connectionString + $";Search Path={schema}");
        if (legacyLedger)
        {
            await ExecuteAsync(dataSource, HotCacheSchema.CreateLedgerSql);
            for (var index = 0; index < 4; index++)
            {
                await ExecuteAsync(dataSource, HotCacheSchema.Migrations[index].Sql);
            }

            await ExecuteAsync(dataSource, "INSERT INTO hot_cache_schema_migrations(version) VALUES (1)");
        }

        await PostgreSqlHotCacheSchemaMigrator.ApplyAsync(dataSource, CancellationToken.None);
        await PostgreSqlHotCacheSchemaMigrator.ApplyAsync(dataSource, CancellationToken.None);
        await using (var versions = dataSource.CreateCommand("SELECT array_agg(version ORDER BY version) FROM hot_cache_schema_migrations"))
        {
            Assert.Equal(new[] { 1, 2, 3, 4, 5 }, (int[])(await versions.ExecuteScalarAsync())!);
        }

        var store = new PostgreSqlHotCacheJobStore(dataSource);
        var promotion = Guid.NewGuid();
        var protectedItem = Guid.NewGuid();
        await ExecuteAsync(dataSource, $"INSERT INTO hot_cache_jobs(id,item_id,kind,canonical_path,source_length,source_modified_utc,priority) VALUES('{promotion}','{Guid.NewGuid()}','promotion','/media/a.mkv',1,now(),1); INSERT INTO hot_cache_jobs(id,item_id,kind,state,canonical_path,hot_path,source_length,source_modified_utc,is_pinned) VALUES('{protectedItem}','{Guid.NewGuid()}','promotion','completed','/media/b.mkv','/hot/b.mkv',1,now(),true);");
        var claimed = await store.ClaimAsync("worker", TimeSpan.FromMinutes(1), CancellationToken.None);
        Assert.Equal(promotion, claimed?.Id);
        Assert.True(await store.CompleteAsync(promotion, "worker", "/hot/a.mkv", "cephfs", CancellationToken.None));
        Assert.Null(await store.ClaimEvictionAsync("worker", TimeSpan.FromMinutes(1), CancellationToken.None));
        await ExecuteAsync(dataSource, $"UPDATE hot_cache_jobs SET is_pinned=false WHERE id='{protectedItem}'; INSERT INTO hot_cache_playback_leases(play_session_id,item_id,expires_at_utc) SELECT 'live',item_id,now()+interval '1 hour' FROM hot_cache_jobs WHERE id='{protectedItem}'");
        Assert.Null(await store.ClaimEvictionAsync("worker", TimeSpan.FromMinutes(1), CancellationToken.None));
        await ExecuteAsync(dataSource, "UPDATE hot_cache_playback_leases SET expires_at_utc=now()-interval '1 second'");
        Assert.Equal(protectedItem, (await store.ClaimEvictionAsync("worker", TimeSpan.FromMinutes(1), CancellationToken.None))?.Id);
        await ExecuteAsync(dataSource, $"INSERT INTO hot_cache_events(job_id,kind,detail,created_at) VALUES('{promotion}','old','old',now()-interval '31 days'); INSERT INTO hot_cache_admin_history(kind,detail,created_at) VALUES('copied','old',now()-interval '31 days')");
        await store.SnapshotAsync(CancellationToken.None);
        await using var retained = dataSource.CreateCommand("SELECT (SELECT count(*) FROM hot_cache_events WHERE kind='old'),(SELECT count(*) FROM hot_cache_admin_history WHERE detail='old')");
        await using var reader = await retained.ExecuteReaderAsync();
        await reader.ReadAsync();
        Assert.Equal(0L, reader.GetInt64(0));
        Assert.Equal(0L, reader.GetInt64(1));

        await using var drop = admin.CreateCommand($"DROP SCHEMA {schema} CASCADE");
        await drop.ExecuteNonQueryAsync();
    }

    private static async Task ExecuteAsync(NpgsqlDataSource dataSource, string sql)
    {
        await using var command = dataSource.CreateCommand(sql);
        await command.ExecuteNonQueryAsync();
    }
}
