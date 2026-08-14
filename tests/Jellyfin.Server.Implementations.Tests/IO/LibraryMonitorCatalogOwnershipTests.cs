using System;
using System.Threading;
using Emby.Server.Implementations.IO;
using MediaBrowser.Controller.Configuration;
using MediaBrowser.Controller.Library;
using MediaBrowser.Model.IO;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Xunit;

namespace Jellyfin.Server.Implementations.Tests.IO;

/// <summary>
/// Tests filesystem refresh dispatch at the catalog ownership seam.
/// </summary>
public sealed class LibraryMonitorCatalogOwnershipTests
{
    /// <summary>
    /// A follower ignores a filesystem event before it can dispatch catalog refresh work.
    /// </summary>
    [Fact]
    public void ReportFileSystemChanged_FollowerDoesNotDispatchRefresh()
    {
        var fileSystem = new Mock<IFileSystem>(MockBehavior.Strict);
        using var monitor = CreateMonitor(isOwner: false, fileSystem.Object);

        monitor.ReportFileSystemChanged("/media/movies/new-file.mkv");

        fileSystem.VerifyNoOtherCalls();
    }

    /// <summary>
    /// The same monitor instance accepts filesystem events immediately after becoming owner.
    /// </summary>
    [Fact]
    public void ReportFileSystemChanged_OwnershipChangeNeedsNoRestart()
    {
        var ownership = new MutableCatalogOwnership();
        var fileSystem = new Mock<IFileSystem>();
        fileSystem
            .Setup(i => i.GetFileSystemInfo("/media/movies/new-file.mkv"))
            .Returns(new FileSystemMetadata
            {
                Exists = true,
                FullName = "/media/movies/new-file.mkv",
                Name = "new-file.mkv"
            });
        using var monitor = CreateMonitor(ownership, fileSystem.Object);

        monitor.ReportFileSystemChanged("/media/movies/new-file.mkv");
        ownership.IsOwner = true;
        monitor.ReportFileSystemChanged("/media/movies/new-file.mkv");

        fileSystem.Verify(i => i.GetFileSystemInfo("/media/movies/new-file.mkv"), Times.Once);
    }

    private static LibraryMonitor CreateMonitor(bool isOwner, IFileSystem fileSystem)
        => CreateMonitor(new MutableCatalogOwnership { IsOwner = isOwner }, fileSystem);

    private static LibraryMonitor CreateMonitor(ICatalogOwnership ownership, IFileSystem fileSystem)
    {
        var lifetime = new Mock<IHostApplicationLifetime>();
        lifetime.SetupGet(i => i.ApplicationStarted).Returns(CancellationToken.None);
        return new LibraryMonitor(
            NullLogger<LibraryMonitor>.Instance,
            Mock.Of<ILibraryManager>(),
            Mock.Of<IServerConfigurationManager>(),
            fileSystem,
            ownership,
            lifetime.Object);
    }

    private sealed class MutableCatalogOwnership : ICatalogOwnership
    {
        public bool IsOwner { get; set; }

        public bool TryGetCatalogWriteToken(out CancellationToken ownershipLost)
        {
            ownershipLost = CancellationToken.None;
            return IsOwner;
        }
    }
}
