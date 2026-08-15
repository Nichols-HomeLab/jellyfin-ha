using System;
using System.IO;
using Emby.Server.Implementations.Library;
using MediaBrowser.Controller.Library;
using MediaBrowser.Model.Dto;
using MediaBrowser.Model.Entities;
using MediaBrowser.Model.MediaInfo;
using Xunit;

namespace Jellyfin.Server.Implementations.Tests.Library;

public sealed class HotCacheDirectPlayTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "hot-direct-" + Guid.NewGuid().ToString("N"));

    [Fact]
    public void TransientDirectPlayPathReturnsHotThenColdWithoutChangingCanonicalPath()
    {
        var media = Path.Combine(_root, "media");
        var hotRoot = Path.Combine(_root, "hot");
        Directory.CreateDirectory(media);
        Directory.CreateDirectory(hotRoot);
        var canonical = Path.Combine(media, "episode.mkv");
        var hot = Path.Combine(hotRoot, "episode.mkv");
        File.WriteAllText(canonical, "bytes");
        File.Copy(canonical, hot);
        File.SetLastWriteTimeUtc(hot, File.GetLastWriteTimeUtc(canonical));
        var source = new MediaSourceInfo { Path = canonical, Size = new FileInfo(canonical).Length, Protocol = MediaProtocol.File };
        var resolver = new HotCachePlaybackPathResolver(media, hotRoot, new NullHotCacheCoordinator());
        MediaSourceManager.ApplyPlaybackPathResolution([source], resolver);
        Assert.Equal(hot, source.Path);
        Assert.Equal(canonical, source.CanonicalPath);
        File.Delete(hot);
        MediaSourceManager.ApplyPlaybackPathResolution([source], resolver);
        Assert.Equal(canonical, source.Path);
        Assert.Equal(canonical, source.CanonicalPath);
    }

    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, true);
        }
    }
}
