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
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.Persistence;
using MediaBrowser.Model.Entities;
using Moq;
using Xunit;

namespace Jellyfin.Server.Implementations.Tests.Library.LibraryManager;

public sealed class CatalogOwnershipMutationTests
{
    [Theory]
    [InlineData("create")]
    [InlineData("update")]
    [InlineData("delete")]
    public async Task CatalogMutation_WhenFollower_RejectsWithoutRepositoryWrite(string mutation)
    {
        var (libraryManager, repository) = CreateLibraryManager(new TestCatalogOwnership(false));
        var item = new Folder { Name = "Catalog item" };

        await Assert.ThrowsAsync<CatalogWriteUnavailableException>(async () =>
        {
            switch (mutation)
            {
                case "create":
                    libraryManager.CreateItems([item], null, CancellationToken.None);
                    break;
                case "update":
                    await libraryManager.UpdateItemsAsync([item], item, ItemUpdateType.MetadataEdit, CancellationToken.None);
                    break;
                case "delete":
                    libraryManager.DeleteItem(item, new DeleteOptions());
                    break;
            }
        });

        repository.Verify(r => r.SaveItems(It.IsAny<IReadOnlyList<BaseItem>>(), It.IsAny<CancellationToken>()), Times.Never);
        repository.Verify(r => r.DeleteItem(It.IsAny<IReadOnlyList<Guid>>()), Times.Never);
    }

    [Fact]
    public void CreateItems_WhenOwnershipIsLost_CancelsRepositoryWrite()
    {
        using var ownership = new TestCatalogOwnership(true);
        var (libraryManager, repository) = CreateLibraryManager(ownership);
        repository
            .Setup(r => r.SaveItems(It.IsAny<IReadOnlyList<BaseItem>>(), It.IsAny<CancellationToken>()))
            .Callback<IReadOnlyList<BaseItem>, CancellationToken>((_, token) =>
            {
                ownership.LoseOwnership();
                token.ThrowIfCancellationRequested();
            });

        Assert.Throws<OperationCanceledException>(() =>
            libraryManager.CreateItems([new Folder { Name = "Catalog item" }], null, CancellationToken.None));
    }

    private static (Emby.Server.Implementations.Library.LibraryManager Manager, Mock<IItemRepository> Repository) CreateLibraryManager(
        ICatalogOwnership ownership)
    {
        var fixture = new Fixture().Customize(new AutoMoqCustomization());
        fixture.Register(() => new NamingOptions());
        fixture.Inject(ownership);
        var config = fixture.Freeze<Mock<IServerConfigurationManager>>();
        config.Setup(c => c.Configuration).Returns(new MediaBrowser.Model.Configuration.ServerConfiguration());
        var repository = fixture.Freeze<Mock<IItemRepository>>();

        var constructor = typeof(Emby.Server.Implementations.Library.LibraryManager)
            .GetConstructors()
            .Single(c => c.GetParameters().Any(p => p.ParameterType == typeof(ICatalogOwnership)));
        var context = new SpecimenContext(fixture);
        var arguments = constructor
            .GetParameters()
            .Select(p => p.ParameterType == typeof(ICatalogOwnership) ? ownership : context.Resolve(p.ParameterType))
            .ToArray();

        return ((Emby.Server.Implementations.Library.LibraryManager)constructor.Invoke(arguments), repository);
    }

    private sealed class TestCatalogOwnership(bool isOwner) : ICatalogOwnership, IDisposable
    {
        private readonly CancellationTokenSource _ownershipLost = new();

        public bool IsOwner { get; private set; } = isOwner;

        public bool TryGetCatalogWriteToken(out CancellationToken ownershipLost)
        {
            ownershipLost = _ownershipLost.Token;
            return IsOwner;
        }

        public void LoseOwnership()
        {
            IsOwner = false;
            _ownershipLost.Cancel();
        }

        public void Dispose() => _ownershipLost.Dispose();
    }
}
