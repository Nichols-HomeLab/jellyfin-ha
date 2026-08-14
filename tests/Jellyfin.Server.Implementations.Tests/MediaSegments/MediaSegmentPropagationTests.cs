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

    public void Dispose() => _connection.Dispose();

    private sealed class RecordingCatalogChangeNotifier : ICatalogChangeNotifier
    {
        public List<CatalogChange> Published { get; } = [];

        public event Action<CatalogChange>? Changed
        {
            add { }
            remove { }
        }

        public void Publish(CatalogChange change) => Published.Add(change);
    }
}
