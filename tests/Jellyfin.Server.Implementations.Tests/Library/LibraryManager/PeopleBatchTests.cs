using System;
using System.Collections.Generic;
using System.Linq;
using AutoFixture;
using AutoFixture.AutoMoq;
using Emby.Naming.Common;
using MediaBrowser.Controller.Configuration;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.Persistence;
using MediaBrowser.Model.Configuration;
using MediaBrowser.Model.IO;
using Moq;
using Xunit;

namespace Jellyfin.Server.Implementations.Tests.Library.LibraryManager;

[Collection(nameof(PeopleBatchTestCollection))]
public sealed class PeopleBatchTests : IDisposable
{
    private readonly IServerConfigurationManager _originalConfigurationManager = BaseItem.ConfigurationManager;
    private readonly IFileSystem _originalFileSystem = BaseItem.FileSystem;

    [Fact]
    public void GetPeopleItems_ResolvesAllNamesWithOneItemQuery()
    {
        var fixture = new Fixture().Customize(new AutoMoqCustomization());
        fixture.Register(() => new NamingOptions());

        var config = fixture.Freeze<Mock<IServerConfigurationManager>>();
        config.SetupGet(manager => manager.Configuration).Returns(new ServerConfiguration
        {
            CacheSize = 100,
            EnableNormalizedItemByNameIds = true
        });
        config.SetupGet(manager => manager.ApplicationPaths.ProgramDataPath).Returns("/data");
        config.SetupGet(manager => manager.ApplicationPaths.PeoplePath).Returns("/data/people");

        var peopleRepository = fixture.Freeze<Mock<IPeopleRepository>>();
        peopleRepository
            .Setup(repository => repository.GetPeopleNames(It.IsAny<InternalPeopleQuery>()))
            .Returns(["Alice Example", "Bob Example"]);

        var itemRepository = fixture.Freeze<Mock<IItemRepository>>();
        itemRepository
            .Setup(repository => repository.GetItemList(It.IsAny<InternalItemsQuery>()))
            .Returns((InternalItemsQuery query) =>
            [
                new Person { Id = query.ItemIds[1], Name = "Bob Example" },
                new Person { Id = query.ItemIds[0], Name = "Alice Example" }
            ]);

        var fileSystem = fixture.Freeze<Mock<IFileSystem>>();
        fileSystem.Setup(system => system.GetValidFilename(It.IsAny<string>())).Returns<string>(name => name);

        var libraryManager = fixture.Create<Emby.Server.Implementations.Library.LibraryManager>();
        BaseItem.ConfigurationManager = config.Object;
        BaseItem.FileSystem = fileSystem.Object;

        var result = libraryManager.GetPeopleItems(new InternalPeopleQuery());

        Assert.Equal(["Alice Example", "Bob Example"], result.Select(person => person.Name));
        itemRepository.Verify(
            repository => repository.GetItemList(It.Is<InternalItemsQuery>(query => query.ItemIds.Length == 2)),
            Times.Once);
        itemRepository.Verify(repository => repository.RetrieveItem(It.IsAny<Guid>()), Times.Never);
    }

    public void Dispose()
    {
        BaseItem.ConfigurationManager = _originalConfigurationManager;
        BaseItem.FileSystem = _originalFileSystem;
    }
}
