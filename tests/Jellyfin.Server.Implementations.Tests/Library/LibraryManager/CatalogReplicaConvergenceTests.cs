using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using AutoFixture;
using AutoFixture.AutoMoq;
using AutoFixture.Kernel;
using Emby.Naming.Common;
using MediaBrowser.Controller.Configuration;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.Movies;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.Persistence;
using MediaBrowser.Model.Entities;
using Moq;
using Xunit;

namespace Jellyfin.Server.Implementations.Tests.Library.LibraryManager;

public sealed class CatalogReplicaConvergenceTests
{
    [Fact]
    public async Task OwnerUpdate_EvictsFollowerItemAndReplaysLocalUpdate()
    {
        var hub = new FakeCatalogChangeHub();
        var ownerNotifier = hub.CreateNotifier();
        var followerNotifier = hub.CreateNotifier();
        var itemId = Guid.NewGuid();
        var parentId = Guid.NewGuid();
        var persistedName = "[tmdbid-739405]";
        var ownerItem = new Movie { Id = itemId, Name = "Operation Fortune: Ruse de Guerre" };
        var parent = new Folder { Id = parentId, Name = "Movies" };

        var (owner, ownerRepository) = CreateLibraryManager(ownerNotifier);
        var (follower, followerRepository) = CreateLibraryManager(followerNotifier);
        followerRepository
            .Setup(r => r.RetrieveItem(itemId))
            .Returns(() => new Movie { Id = itemId, Name = persistedName });
        followerRepository
            .Setup(r => r.RetrieveItem(parentId))
            .Returns(() => new Folder { Id = parentId, Name = "Movies" });
        ownerRepository
            .Setup(r => r.SaveItems(It.IsAny<IReadOnlyList<BaseItem>>(), It.IsAny<CancellationToken>()))
            .Callback<IReadOnlyList<BaseItem>, CancellationToken>((items, _) => persistedName = items.Single().Name);

        Assert.Equal("[tmdbid-739405]", follower.GetItemById(itemId)!.Name);
        BaseItem? replayedItem = null;
        follower.ItemUpdated += (_, eventArgs) => replayedItem = eventArgs.Item;

        await owner.UpdateItemsAsync([ownerItem], parent, ItemUpdateType.MetadataEdit, CancellationToken.None);

        Assert.Equal("Operation Fortune: Ruse de Guerre", follower.GetItemById(itemId)!.Name);
        Assert.Equal(itemId, replayedItem?.Id);
        Assert.Equal(
            2,
            followerRepository.Invocations.Count(i =>
                i.Method.Name == nameof(IItemRepository.RetrieveItem)
                && (Guid)i.Arguments[0] == itemId));
    }

    [Fact]
    public void DuplicateRemoteChange_IsAppliedOnce()
    {
        var notifier = new FakeCatalogChangeNotifier();
        var (follower, repository) = CreateLibraryManager(notifier);
        var itemId = Guid.NewGuid();
        var parentId = Guid.NewGuid();
        var item = new Movie { Id = itemId, Name = "Updated" };
        repository.Setup(r => r.RetrieveItem(itemId)).Returns(item);
        repository.Setup(r => r.RetrieveItem(parentId)).Returns(new Folder { Id = parentId, Name = "Movies" });
        var replayCount = 0;
        follower.ItemUpdated += (_, _) => replayCount++;
        var change = new CatalogChange(12, CatalogChangeKind.Updated, itemId, parentId);

        notifier.Receive(change);
        notifier.Receive(change);

        Assert.Equal(1, replayCount);
    }

    [Fact]
    public void RemoteImageChange_ReplacesCachedImageMetadata()
    {
        var notifier = new FakeCatalogChangeNotifier();
        var (follower, repository) = CreateLibraryManager(notifier);
        var itemId = Guid.NewGuid();
        var parentId = Guid.NewGuid();
        var persisted = new Movie { Id = itemId, Name = "The Return of the King" };
        repository.Setup(r => r.RetrieveItem(itemId)).Returns(() => persisted);
        repository.Setup(r => r.RetrieveItem(parentId)).Returns(new Folder { Id = parentId, Name = "Movies" });

        Assert.Empty(follower.GetItemById(itemId)!.ImageInfos);
        persisted = new Movie
        {
            Id = itemId,
            Name = "The Return of the King",
            ImageInfos = [new ItemImageInfo { Path = "/metadata/return-of-the-king.jpg", Type = ImageType.Primary }]
        };

        notifier.Receive(new CatalogChange(21, CatalogChangeKind.Updated, itemId, parentId));

        Assert.Equal("/metadata/return-of-the-king.jpg", follower.GetItemById(itemId)!.ImageInfos.Single().Path);
    }

    [Fact]
    public void FullResync_DiscardsAllCachedCatalogItemsWithoutPublishingOrWriting()
    {
        var notifier = new FakeCatalogChangeNotifier();
        var (follower, repository) = CreateLibraryManager(notifier);
        var itemId = Guid.NewGuid();
        var persistedName = "Before reconnect";
        repository.Setup(r => r.RetrieveItem(itemId)).Returns(() => new Movie { Id = itemId, Name = persistedName });

        Assert.Equal("Before reconnect", follower.GetItemById(itemId)!.Name);
        persistedName = "After reconnect";
        notifier.Receive(CatalogChange.FullResync(31));

        Assert.Equal("After reconnect", follower.GetItemById(itemId)!.Name);
        Assert.Empty(notifier.Published);
        repository.Verify(r => r.SaveItems(It.IsAny<IReadOnlyList<BaseItem>>(), It.IsAny<CancellationToken>()), Times.Never);
        repository.Verify(r => r.SaveImages(It.IsAny<BaseItem>()), Times.Never);
    }

    private static (Emby.Server.Implementations.Library.LibraryManager Manager, Mock<IItemRepository> Repository) CreateLibraryManager(
        ICatalogChangeNotifier notifier)
    {
        var fixture = new Fixture().Customize(new AutoMoqCustomization());
        fixture.Register(() => new NamingOptions());
        fixture.Inject<ICatalogOwnership>(new SingleInstanceCatalogOwnership());
        fixture.Inject(notifier);
        var config = fixture.Freeze<Mock<IServerConfigurationManager>>();
        config.Setup(c => c.Configuration).Returns(new MediaBrowser.Model.Configuration.ServerConfiguration());
        var repository = fixture.Freeze<Mock<IItemRepository>>();

        var constructor = typeof(Emby.Server.Implementations.Library.LibraryManager)
            .GetConstructors()
            .Single(c => c.GetParameters().Any(p => p.ParameterType == typeof(ICatalogChangeNotifier)));
        var context = new SpecimenContext(fixture);
        var arguments = constructor
            .GetParameters()
            .Select(p => p.ParameterType == typeof(ICatalogChangeNotifier) ? notifier : context.Resolve(p.ParameterType))
            .ToArray();

        return ((Emby.Server.Implementations.Library.LibraryManager)constructor.Invoke(arguments), repository);
    }

    private sealed class FakeCatalogChangeHub
    {
        private readonly List<FakeCatalogChangeNotifier> _notifiers = [];
        private long _sequence;

        public FakeCatalogChangeNotifier CreateNotifier()
        {
            var notifier = new FakeCatalogChangeNotifier(this);
            _notifiers.Add(notifier);
            return notifier;
        }

        public void Publish(FakeCatalogChangeNotifier source, CatalogChange change)
        {
            change = change with { Sequence = Interlocked.Increment(ref _sequence) };
            foreach (var notifier in _notifiers)
            {
                if (!ReferenceEquals(source, notifier))
                {
                    notifier.Receive(change);
                }
            }
        }
    }

    private sealed class FakeCatalogChangeNotifier(FakeCatalogChangeHub? hub = null) : ICatalogChangeNotifier
    {
        public List<CatalogChange> Published { get; } = [];

        public event Action<CatalogChange>? Changed;

        public void Publish(CatalogChange change)
        {
            Published.Add(change);
            hub?.Publish(this, change);
        }

        public void Receive(CatalogChange change)
            => Changed?.Invoke(change);
    }
}
