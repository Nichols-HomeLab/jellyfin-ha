using System;
using System.Threading;
using AutoFixture;
using AutoFixture.AutoMoq;
using Emby.Naming.Common;
using MediaBrowser.Controller.Configuration;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.Persistence;
using MediaBrowser.Model.Configuration;
using Moq;
using Xunit;

namespace Jellyfin.Server.Implementations.Tests.Library.LibraryManager;

public sealed class LinkedChildCatalogOwnershipTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void UpsertLinkedChild_RequiresOwnershipAndPublishesParentChange(bool isOwner)
    {
        var fixture = new Fixture().Customize(new AutoMoqCustomization());
        fixture.Register(() => new NamingOptions());
        fixture.Inject<ICatalogOwnership>(new Ownership(isOwner));
        fixture.Freeze<Mock<IServerConfigurationManager>>()
            .SetupGet(config => config.Configuration).Returns(new ServerConfiguration());
        var linkedChildren = fixture.Freeze<Mock<ILinkedChildrenService>>();
        var notifier = fixture.Freeze<Mock<ICatalogChangeNotifier>>();
        var repository = fixture.Freeze<Mock<IItemRepository>>();
        var parent = new Video { Id = Guid.NewGuid(), ParentId = Guid.NewGuid() };
        var childId = Guid.NewGuid();
        repository.Setup(repo => repo.RetrieveItem(parent.Id)).Returns(parent);
        var manager = fixture.Create<Emby.Server.Implementations.Library.LibraryManager>();

        if (isOwner)
        {
            manager.UpsertLinkedChild(parent.Id, childId, LinkedChildType.LocalAlternateVersion);
            linkedChildren.Verify(service => service.UpsertLinkedChild(parent.Id, childId, LinkedChildType.LocalAlternateVersion), Times.Once);
            notifier.Verify(
                service => service.Publish(It.Is<CatalogChange>(change =>
                    change.Kind == CatalogChangeKind.Updated && change.ItemId.Equals(parent.Id) && change.ParentId.Equals(parent.ParentId))),
                Times.Once);
        }
        else
        {
            Assert.Throws<CatalogWriteUnavailableException>(() => manager.UpsertLinkedChild(parent.Id, childId, LinkedChildType.LocalAlternateVersion));
            linkedChildren.VerifyNoOtherCalls();
            notifier.Verify(service => service.Publish(It.IsAny<CatalogChange>()), Times.Never);
        }
    }

    [Fact]
    public void DeleteOrphanedCredits_WhenFollower_RejectsWithoutDeleting()
    {
        var fixture = new Fixture().Customize(new AutoMoqCustomization());
        fixture.Register(() => new NamingOptions());
        fixture.Inject<ICatalogOwnership>(new Ownership(false));
        fixture.Freeze<Mock<IServerConfigurationManager>>()
            .SetupGet(config => config.Configuration).Returns(new ServerConfiguration());
        var people = fixture.Freeze<Mock<IPeopleRepository>>();
        var manager = fixture.Create<Emby.Server.Implementations.Library.LibraryManager>();

        Assert.Throws<CatalogWriteUnavailableException>(() => manager.DeleteOrphanedCredits());
        people.VerifyNoOtherCalls();
    }

    private sealed class Ownership(bool isOwner) : ICatalogOwnership
    {
        public bool TryGetCatalogWriteToken(out CancellationToken ownershipLost)
        {
            ownershipLost = CancellationToken.None;
            return isOwner;
        }
    }
}
