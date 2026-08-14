using System;
using System.IO;
using System.Security.Claims;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Api.Controllers;
using MediaBrowser.Common.Configuration;
using MediaBrowser.Controller.Configuration;
using MediaBrowser.Controller.Drawing;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.Movies;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.Providers;
using MediaBrowser.Model.Entities;
using MediaBrowser.Model.IO;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;
using Moq;
using Xunit;

namespace Jellyfin.Api.Tests.Controllers;

public static class ImageControllerTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public static async Task ItemImageDeleteOrSwap_WhenFollower_PreservesSharedImages(bool swap)
    {
        var directory = Directory.CreateTempSubdirectory("jellyfin-image-owner-");
        var firstPath = Path.Combine(directory.FullName, "backdrop.jpg");
        var secondPath = Path.Combine(directory.FullName, "backdrop1.jpg");
        await File.WriteAllBytesAsync(firstPath, [1, 2, 3]);
        await File.WriteAllBytesAsync(secondPath, [4, 5, 6]);

        try
        {
            var itemId = Guid.NewGuid();
            var item = new Movie
            {
                Id = itemId,
                ImageInfos =
                [
                    new ItemImageInfo { Path = firstPath, Type = ImageType.Backdrop },
                    new ItemImageInfo { Path = secondPath, Type = ImageType.Backdrop }
                ]
            };
            var libraryManager = new Mock<ILibraryManager>();
            libraryManager.Setup(l => l.GetItemById<BaseItem>(itemId, Guid.Empty)).Returns(item);
            var controller = CreateController(libraryManager.Object, new FollowerCatalogOwnership());

            Task<ActionResult> mutation = swap
                ? controller.UpdateItemImageIndex(itemId, ImageType.Backdrop, 0, 1)
                : controller.DeleteItemImage(itemId, ImageType.Backdrop, 0);

            await Assert.ThrowsAsync<CatalogWriteUnavailableException>(() => mutation);
            Assert.Equal([1, 2, 3], await File.ReadAllBytesAsync(firstPath));
            Assert.Equal([4, 5, 6], await File.ReadAllBytesAsync(secondPath));
        }
        finally
        {
            directory.Delete(true);
        }
    }

    [Theory]
    [InlineData("image/apng", ".apng")]
    [InlineData("image/avif", ".avif")]
    [InlineData("image/bmp", ".bmp")]
    [InlineData("image/gif", ".gif")]
    [InlineData("image/x-icon", ".ico")]
    [InlineData("image/jpeg", ".jpg")]
    [InlineData("image/png", ".png")]
    [InlineData("image/png; charset=utf-8", ".png")]
    [InlineData("image/svg+xml", ".svg")]
    [InlineData("image/tiff", ".tiff")]
    [InlineData("image/webp", ".webp")]
    public static void TryGetImageExtensionFromContentType_Valid_True(string contentType, string extension)
    {
        Assert.True(ImageController.TryGetImageExtensionFromContentType(contentType, out var ex));
        Assert.Equal(extension, ex);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("text/html")]
    public static void TryGetImageExtensionFromContentType_InValid_False(string? contentType)
    {
        Assert.False(ImageController.TryGetImageExtensionFromContentType(contentType, out var ex));
        Assert.Null(ex);
    }

    private static ImageController CreateController(ILibraryManager libraryManager, ICatalogOwnership ownership)
    {
        var controller = new ImageController(
            Mock.Of<IUserManager>(),
            libraryManager,
            Mock.Of<IProviderManager>(),
            Mock.Of<IImageProcessor>(),
            Mock.Of<IFileSystem>(),
            Mock.Of<ILogger<ImageController>>(),
            Mock.Of<IServerConfigurationManager>(),
            Mock.Of<IApplicationPaths>(),
            ownership);
        controller.ControllerContext = new ControllerContext
        {
            HttpContext = new DefaultHttpContext
            {
                User = new ClaimsPrincipal(new ClaimsIdentity())
            }
        };
        return controller;
    }

    private sealed class FollowerCatalogOwnership : ICatalogOwnership
    {
        public bool TryGetCatalogWriteToken(out CancellationToken ownershipLost)
        {
            ownershipLost = CancellationToken.None;
            return false;
        }
    }
}
