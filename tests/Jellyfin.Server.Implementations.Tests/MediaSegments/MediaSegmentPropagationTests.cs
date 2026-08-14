using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Database.Implementations;
using Jellyfin.Database.Implementations.Locking;
using Jellyfin.Database.Providers.Sqlite;
using Jellyfin.Server.Implementations.MediaSegments;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.MediaSegments;
using MediaBrowser.Model.MediaSegments;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Xunit;

namespace Jellyfin.Server.Implementations.Tests.MediaSegments;

public sealed class MediaSegmentPropagationTests : IDisposable
{
    private readonly SqliteConnection _connection;
    private readonly Mock<IDbContextFactory<JellyfinDbContext>> _factory;

    public MediaSegmentPropagationTests()
    {
        _connection = new SqliteConnection("Data Source=:memory:");
        _connection.Open();
        var options = new DbContextOptionsBuilder<JellyfinDbContext>()
            .UseSqlite(_connection)
            .Options;
        JellyfinDbContext CreateContext() => new(
            options,
            NullLogger<JellyfinDbContext>.Instance,
            new SqliteDatabaseProvider(null!, NullLogger<SqliteDatabaseProvider>.Instance),
            new NoLockBehavior(NullLogger<NoLockBehavior>.Instance));
        using var context = CreateContext();
        context.Database.EnsureCreated();
        _factory = new Mock<IDbContextFactory<JellyfinDbContext>>();
        _factory.Setup(factory => factory.CreateDbContext()).Returns(CreateContext);
        _factory.Setup(factory => factory.CreateDbContextAsync(It.IsAny<CancellationToken>())).ReturnsAsync(CreateContext);
    }

    [Fact]
    public async Task CreateAndDeleteSegments_PublishItemScopedChanges()
    {
        var notifier = new RecordingCatalogChangeNotifier();
        var manager = new MediaSegmentManager(
            NullLogger<MediaSegmentManager>.Instance,
            _factory.Object,
            Array.Empty<IMediaSegmentProvider>(),
            notifier);
        var itemId = Guid.NewGuid();
        var segment = new MediaSegmentDto
        {
            Id = Guid.NewGuid(),
            ItemId = itemId,
            StartTicks = 100,
            EndTicks = 200
        };

        await manager.CreateSegmentAsync(segment, "intro-skipper");
        await manager.DeleteSegmentsAsync(itemId, CancellationToken.None);

        Assert.Collection(
            notifier.Published,
            change => Assert.Equal((CatalogChangeKind.MediaSegments, itemId), (change.Kind, change.ItemId)),
            change => Assert.Equal((CatalogChangeKind.MediaSegments, itemId), (change.Kind, change.ItemId)));
    }

    [Theory]
    [InlineData("create")]
    [InlineData("delete-one")]
    [InlineData("delete-all")]
    [InlineData("providers")]
    public async Task SegmentMutation_WhenFollower_RejectsWithoutDatabaseWriteOrPublish(string mutation)
    {
        var notifier = new RecordingCatalogChangeNotifier();
        var manager = new MediaSegmentManager(
            NullLogger<MediaSegmentManager>.Instance,
            _factory.Object,
            Array.Empty<IMediaSegmentProvider>(),
            notifier,
            new TestCatalogOwnership(false));
        var itemId = Guid.NewGuid();

        await Assert.ThrowsAsync<CatalogWriteUnavailableException>(async () =>
        {
            switch (mutation)
            {
                case "create":
                    await manager.CreateSegmentAsync(
                        new MediaSegmentDto { Id = Guid.NewGuid(), ItemId = itemId, StartTicks = 1, EndTicks = 2 },
                        "intro-skipper");
                    break;
                case "delete-one":
                    await manager.DeleteSegmentAsync(Guid.NewGuid());
                    break;
                case "delete-all":
                    await manager.DeleteSegmentsAsync(itemId, CancellationToken.None);
                    break;
                case "providers":
                    await manager.RunSegmentPluginProviders(
                        new MediaBrowser.Controller.Entities.Movies.Movie { Id = itemId },
                        new MediaBrowser.Model.Configuration.LibraryOptions(),
                        false,
                        CancellationToken.None);
                    break;
            }
        });

        _factory.Verify(factory => factory.CreateDbContext(), Times.Never);
        _factory.Verify(factory => factory.CreateDbContextAsync(It.IsAny<CancellationToken>()), Times.Never);
        Assert.Empty(notifier.Published);
    }

    public void Dispose() => _connection.Dispose();

    private sealed class RecordingCatalogChangeNotifier : ICatalogChangeNotifier
    {
        public event Action<CatalogChange>? Changed
        {
            add { }
            remove { }
        }

        public List<CatalogChange> Published { get; } = [];

        public void Publish(CatalogChange change) => Published.Add(change);
    }

    private sealed class TestCatalogOwnership(bool isOwner) : ICatalogOwnership
    {
        public bool TryGetCatalogWriteToken(out CancellationToken ownershipLost)
        {
            ownershipLost = CancellationToken.None;
            return isOwner;
        }
    }
}
