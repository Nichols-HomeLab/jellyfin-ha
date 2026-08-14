using System;
using System.Linq;
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
    private readonly PostgreSqlContainer _container;

    /// <summary>
    /// Initializes a new instance of the <see cref="PostgreSqlCatalogOwnershipTests"/> class.
    /// </summary>
    public PostgreSqlCatalogOwnershipTests()
    {
        _container = new PostgreSqlBuilder()
            .WithImage("postgres:16-alpine")
            .WithWaitStrategy(Wait.ForUnixContainer().UntilCommandIsCompleted("pg_isready"))
            .Build();
    }

    /// <inheritdoc />
    public Task InitializeAsync() => _container.StartAsync();

    /// <inheritdoc />
    public Task DisposeAsync() => _container.DisposeAsync().AsTask();

    /// <summary>
    /// Two server instances sharing PostgreSQL expose exactly one writer and hand ownership off after release.
    /// </summary>
    [Fact]
    public async Task SharedAuthority_HasOneOwnerAndHandsOffAfterRelease()
    {
        await using var firstDataSource = NpgsqlDataSource.Create(_container.GetConnectionString());
        await using var secondDataSource = NpgsqlDataSource.Create(_container.GetConnectionString());
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
        Assert.Single(new[] { first, second }.Where(IsOwner));

        var owner = IsOwner(first) ? first : second;
        var follower = ReferenceEquals(owner, first) ? second : first;
        await owner.StopAsync(CancellationToken.None);

        await WaitUntilAsync(() => IsOwner(follower));
        Assert.True(IsOwner(follower));
    }

    /// <summary>
    /// Coordination loss revokes the current ownership token and makes the adapter fail closed.
    /// </summary>
    [Fact]
    public async Task CoordinationUnavailable_RevokesOwnershipAndFailsClosed()
    {
        await using var dataSource = NpgsqlDataSource.Create(_container.GetConnectionString());
        await using var ownership = new PostgreSqlCatalogOwnership(
            dataSource,
            "server-1",
            ProbeInterval,
            NullLogger<PostgreSqlCatalogOwnership>.Instance);
        await ownership.StartAsync(CancellationToken.None);
        await WaitUntilAsync(() => ownership.TryGetCatalogWriteToken(out _));
        Assert.True(ownership.TryGetCatalogWriteToken(out var ownershipLost));

        await _container.StopAsync();

        await WaitUntilAsync(() => !ownership.TryGetCatalogWriteToken(out _));
        Assert.True(ownershipLost.IsCancellationRequested);
        Assert.False(ownership.TryGetCatalogWriteToken(out _));
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
