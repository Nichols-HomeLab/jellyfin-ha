using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using MediaBrowser.Controller.Configuration;
using MediaBrowser.Controller.Entities.Movies;
using MediaBrowser.Controller.Library;
using MediaBrowser.Model.Configuration;
using MediaBrowser.Model.Entities;
using MediaBrowser.Model.IO;
using MediaBrowser.Providers.Manager;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Xunit;

namespace Jellyfin.Providers.Tests.Manager;

public sealed class ImageSaverCatalogOwnershipTests
{
    [Fact]
    public async Task SaveImage_WhenFollower_RejectsBeforeTouchingDestination()
    {
        var destination = Path.Combine(Path.GetTempPath(), $"jellyfin-owner-{Guid.NewGuid():N}.jpg");
        var saver = CreateSaver(new TestCatalogOwnership(false));

        await Assert.ThrowsAsync<CatalogWriteUnavailableException>(() =>
            saver.SaveImage(
                new Movie { Name = "Catalog item" },
                new MemoryStream([1, 2, 3]),
                "image/jpeg",
                ImageType.Primary,
                null,
                CancellationToken.None));

        Assert.False(File.Exists(destination));
    }

    [Fact]
    public async Task SaveImage_WhenSourceAborts_PreservesExistingImageAndRemovesTemporaryFile()
    {
        var directory = Directory.CreateTempSubdirectory("jellyfin-image-atomic-");
        var destination = Path.Combine(directory.FullName, "primary.jpg");
        var original = new byte[] { 9, 8, 7, 6 };
        await File.WriteAllBytesAsync(destination, original);

        try
        {
            var saver = CreateSaver(new TestCatalogOwnership(true));

            await Assert.ThrowsAsync<IOException>(() =>
                saver.SaveImage(new AbortingStream([1, 2, 3]), destination));

            Assert.Equal(original, await File.ReadAllBytesAsync(destination));
            Assert.Equal([destination], Directory.GetFiles(directory.FullName));
        }
        finally
        {
            directory.Delete(true);
        }
    }

    private static ImageSaver CreateSaver(ICatalogOwnership ownership)
    {
        var config = new Mock<IServerConfigurationManager>();
        config.Setup(c => c.Configuration).Returns(new ServerConfiguration());
        return new ImageSaver(
            config.Object,
            Mock.Of<ILibraryMonitor>(),
            Mock.Of<IFileSystem>(),
            NullLogger.Instance,
            ownership);
    }

    private sealed class TestCatalogOwnership(bool isOwner) : ICatalogOwnership
    {
        public bool TryGetCatalogWriteToken(out CancellationToken ownershipLost)
        {
            ownershipLost = CancellationToken.None;
            return isOwner;
        }
    }

    private sealed class AbortingStream(byte[] bytes) : MemoryStream(bytes)
    {
        private bool _readOnce;

        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            if (_readOnce)
            {
                throw new IOException("Simulated aborted request body");
            }

            _readOnce = true;
            return base.ReadAsync(buffer[..Math.Min(1, buffer.Length)], cancellationToken);
        }
    }
}
