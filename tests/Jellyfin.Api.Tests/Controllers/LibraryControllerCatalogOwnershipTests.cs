using System.Threading;
using Jellyfin.Api.Controllers;
using Jellyfin.Api.Models.LibraryDtos;
using MediaBrowser.Controller.Configuration;
using MediaBrowser.Controller.Dto;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.Providers;
using MediaBrowser.Model.Activity;
using MediaBrowser.Model.Globalization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;
using Moq;
using Xunit;

namespace Jellyfin.Api.Tests.Controllers;

public static class LibraryControllerCatalogOwnershipTests
{
    [Fact]
    public static void PostUpdatedMedia_WhenFollower_ReturnsServiceUnavailableWithoutDispatch()
    {
        var libraryMonitor = new Mock<ILibraryMonitor>();
        var controller = CreateController(libraryMonitor.Object, isOwner: false);

        var result = controller.PostUpdatedMedia(CreateUpdate());

        var unavailable = Assert.IsType<StatusCodeResult>(result);
        Assert.Equal(StatusCodes.Status503ServiceUnavailable, unavailable.StatusCode);
        libraryMonitor.VerifyNoOtherCalls();
    }

    [Fact]
    public static void PostUpdatedMedia_WhenOwner_DispatchesAndReturnsNoContent()
    {
        const string Path = "/media/movies/New Movie (2026)";
        var libraryMonitor = new Mock<ILibraryMonitor>();
        var controller = CreateController(libraryMonitor.Object, isOwner: true);

        var result = controller.PostUpdatedMedia(CreateUpdate(Path));

        Assert.IsType<NoContentResult>(result);
        libraryMonitor.Verify(monitor => monitor.ReportFileSystemChanged(Path), Times.Once);
    }

    private static MediaUpdateInfoDto CreateUpdate(string path = "/media/movies/New Movie (2026)")
        => new()
        {
            Updates =
            [
                new MediaUpdateInfoPathDto
                {
                    Path = path,
                    UpdateType = "Created"
                }
            ]
        };

    private static LibraryController CreateController(ILibraryMonitor libraryMonitor, bool isOwner)
        => new(
            Mock.Of<IProviderManager>(),
            Mock.Of<ILibraryManager>(),
            Mock.Of<IUserManager>(),
            Mock.Of<IDtoService>(),
            Mock.Of<IActivityManager>(),
            Mock.Of<ILocalizationManager>(),
            libraryMonitor,
            new TestCatalogOwnership(isOwner),
            Mock.Of<ILogger<LibraryController>>(),
            Mock.Of<IServerConfigurationManager>());

    private sealed class TestCatalogOwnership(bool isOwner) : ICatalogOwnership
    {
        public bool TryGetCatalogWriteToken(out CancellationToken ownershipLost)
        {
            ownershipLost = CancellationToken.None;
            return isOwner;
        }
    }
}
