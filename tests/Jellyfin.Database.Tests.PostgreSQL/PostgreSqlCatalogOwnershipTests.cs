using System;
using System.Threading;
using System.Threading.Tasks;
using DotNet.Testcontainers.Builders;
using Jellyfin.Server.Implementations.Catalog;
using Microsoft.Extensions.Logging.Abstractions;
using Npgsql;
using Testcontainers.PostgreSql;
using Xunit;

namespace Jellyfin.Database.Tests.PostgreSQL;

/// <summary>
/// Integration tests for cluster-wide catalog-writer ownership.
/// </summary>
[Trait("Category", "RequiresDocker")]
public sealed class PostgreSqlCatalogOwnershipTests : IAsyncLifetime
{
    private static readonly TimeSpan ProbeInterval = TimeSpan.FromMilliseconds(50);
    private readonly PostgreSqlContainer? _container;
    private string? _connectionString;

    /// <summary>
    /// Initializes a new instance of the <see cref="PostgreSqlCatalogOwnershipTests"/> class.
    /// </summary>
    public PostgreSqlCatalogOwnershipTests()
    {
        _connectionString = Environment.GetEnvironmentVariable("JELLYFIN_CATALOG_TEST_POSTGRES");
        if (!string.IsNullOrWhiteSpace(_connectionString))
        {
            return;
        }

        _container = new PostgreSqlBuilder("postgres:16-alpine")
            .WithWaitStrategy(Wait.ForUnixContainer().UntilCommandIsCompleted("pg_isready"))
            .Build();
    }

    /// <inheritdoc />
    public async Task InitializeAsync()
    {
        if (_container is not null)
        {
            await _container.StartAsync().ConfigureAwait(false);
            _connectionString = _container.GetConnectionString();
        }
    }

    /// <inheritdoc />
    public async Task DisposeAsync()
    {
        if (_container is not null)
        {
            await _container.DisposeAsync().ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Two server instances sharing PostgreSQL expose exactly one writer and hand ownership off after release.
    /// </summary>
    /// <returns>A <see cref="Task"/> representing the asynchronous operation.</returns>
    [Fact]
    public async Task SharedAuthority_HasOneOwnerAndHandsOffAfterRelease()
    {
        await using var firstDataSource = NpgsqlDataSource.Create(_connectionString!);
        await using var secondDataSource = NpgsqlDataSource.Create(_connectionString!);
        await using var first = new PostgreSqlCatalogOwnership(
            firstDataSource,
            "server-1",
            ProbeInterval,
            NullLogger<PostgreSqlCatalogOwnership>.Instance);
        await using var second = new PostgreSqlCatalogOwnership(
            secondDataSource,
            "server-2",
            ProbeInterval,
            NullLogger<PostgreSqlCatalogOwnership>.Instance);

        await first.StartAsync(CancellationToken.None);
        await second.StartAsync(CancellationToken.None);

        await WaitUntilAsync(() => IsOwner(first) ^ IsOwner(second));
        Assert.Single(new[] { first, second }, IsOwner);

        var owner = IsOwner(first) ? first : second;
        var follower = ReferenceEquals(owner, first) ? second : first;
        await owner.StopAsync(CancellationToken.None);

        await WaitUntilAsync(() => IsOwner(follower));
        Assert.True(IsOwner(follower));
    }

    /// <summary>
    /// Coordination loss revokes the current ownership token before the adapter reacquires ownership.
    /// </summary>
    /// <returns>A <see cref="Task"/> representing the asynchronous operation.</returns>
    [Fact]
    public async Task CoordinationLoss_RevokesOwnershipBeforeReacquiring()
    {
        await using var dataSource = NpgsqlDataSource.Create(_connectionString!);
        await using var ownership = new PostgreSqlCatalogOwnership(
            dataSource,
            "server-1",
            ProbeInterval,
            NullLogger<PostgreSqlCatalogOwnership>.Instance);
        await ownership.StartAsync(CancellationToken.None);
        await WaitUntilAsync(() => ownership.TryGetCatalogWriteToken(out _));
        Assert.True(ownership.TryGetCatalogWriteToken(out var ownershipLost));

        await using var observer = NpgsqlDataSource.Create(_connectionString!);
        await using var command = observer.CreateCommand(
            "SELECT bool_or(pg_terminate_backend(pid)) FROM pg_locks WHERE locktype = 'advisory' AND granted AND classid = $1::oid AND objid = $2::oid");
        command.Parameters.AddWithValue(1246573006);
        command.Parameters.AddWithValue(607);
        Assert.True(await command.ExecuteScalarAsync() is true);

        await WaitUntilAsync(() => ownershipLost.IsCancellationRequested);
        Assert.True(ownershipLost.IsCancellationRequested);

        await WaitUntilAsync(() => ownership.TryGetCatalogWriteToken(out _));
        Assert.True(ownership.TryGetCatalogWriteToken(out _));
    }

    private static bool IsOwner(PostgreSqlCatalogOwnership ownership)
        => ownership.TryGetCatalogWriteToken(out _);

    private static async Task WaitUntilAsync(Func<bool> condition)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        while (!condition())
        {
            await Task.Delay(ProbeInterval, timeout.Token);
        }
    }
}
