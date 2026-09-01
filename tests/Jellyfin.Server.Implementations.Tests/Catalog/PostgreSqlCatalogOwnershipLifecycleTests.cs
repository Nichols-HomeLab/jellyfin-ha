using System;
using System.Threading.Tasks;
using Jellyfin.Server.Implementations.Catalog;
using Microsoft.Extensions.Logging.Abstractions;
using Npgsql;
using Xunit;

namespace Jellyfin.Server.Implementations.Tests.Catalog;

/// <summary>
/// Tests catalog ownership behavior across hosted-service shutdown.
/// </summary>
public sealed class PostgreSqlCatalogOwnershipLifecycleTests
{
    /// <summary>
    /// A late filesystem callback observes fail-closed ownership after the service is disposed.
    /// </summary>
    /// <returns>A <see cref="Task"/> representing the asynchronous operation.</returns>
    [Fact]
    public async Task TryGetCatalogWriteToken_AfterDispose_ReturnsFalseWithoutThrowing()
    {
        await using var dataSource = NpgsqlDataSource.Create(
            "Host=127.0.0.1;Port=1;Database=jellyfin;Username=jellyfin;Timeout=1");
        var ownership = new PostgreSqlCatalogOwnership(
            dataSource,
            "server-1",
            TimeSpan.FromSeconds(1),
            NullLogger<PostgreSqlCatalogOwnership>.Instance);

        await ownership.DisposeAsync();

        var exception = Record.Exception(
            () => Assert.False(ownership.TryGetCatalogWriteToken(out _)));

        Assert.Null(exception);
        Assert.False(ownership.TryGetCatalogWriteToken(out var ownershipLost));
        Assert.True(ownershipLost.IsCancellationRequested);
    }
}
