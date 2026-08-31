using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Database.Implementations;
using Jellyfin.Database.Implementations.Locking;
using Jellyfin.Database.Providers.Sqlite;
using Jellyfin.Server.Implementations.MediaSegments;
using MediaBrowser.Controller.Entities.Movies;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.MediaSegments;
using MediaBrowser.Model;
using MediaBrowser.Model.Configuration;
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
            // The provider dependency is only used for schema behavior outside these in-memory tests.
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
            }
        });

        _factory.Verify(factory => factory.CreateDbContext(), Times.Never);
        _factory.Verify(factory => factory.CreateDbContextAsync(It.IsAny<CancellationToken>()), Times.Never);
        Assert.Empty(notifier.Published);
    }

    [Fact]
    public async Task RunSegmentPluginProviders_WhenFollower_SkipsWithoutProviderOrDatabaseAccess()
    {
        var provider = new Mock<IMediaSegmentProvider>(MockBehavior.Strict);
        var notifier = new RecordingCatalogChangeNotifier();
        var manager = new MediaSegmentManager(
            NullLogger<MediaSegmentManager>.Instance,
            _factory.Object,
            [provider.Object],
            notifier,
            new TestCatalogOwnership(false));
        var item = new Movie { Id = Guid.NewGuid() };

        await manager.RunSegmentPluginProviders(
            item,
            new LibraryOptions(),
            false,
            CancellationToken.None);

        provider.VerifyNoOtherCalls();
        _factory.Verify(factory => factory.CreateDbContext(), Times.Never);
        _factory.Verify(factory => factory.CreateDbContextAsync(It.IsAny<CancellationToken>()), Times.Never);
        Assert.Empty(notifier.Published);
    }

    [Fact]
    public async Task RunSegmentPluginProviders_WhenOwner_InvokesProvider()
    {
        var item = new Movie { Id = Guid.NewGuid() };
        var provider = new Mock<IMediaSegmentProvider>(MockBehavior.Strict);
        provider.SetupGet(candidate => candidate.Name).Returns("intro-skipper");
        provider.Setup(candidate => candidate.Supports(item)).ReturnsAsync(true);
        provider
            .Setup(candidate => candidate.GetMediaSegments(
                It.Is<MediaSegmentGenerationRequest>(request => request.ItemId.Equals(item.Id)),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(Array.Empty<MediaSegmentDto>());
        var manager = new MediaSegmentManager(
            NullLogger<MediaSegmentManager>.Instance,
            _factory.Object,
            [provider.Object],
            new RecordingCatalogChangeNotifier(),
            new TestCatalogOwnership(true));

        await manager.RunSegmentPluginProviders(
            item,
            new LibraryOptions(),
            false,
            CancellationToken.None);

        provider.Verify(candidate => candidate.Supports(item), Times.Once);
        provider.Verify(
            candidate => candidate.GetMediaSegments(
                It.Is<MediaSegmentGenerationRequest>(request => request.ItemId.Equals(item.Id)),
                It.IsAny<CancellationToken>()),
            Times.Once);
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
