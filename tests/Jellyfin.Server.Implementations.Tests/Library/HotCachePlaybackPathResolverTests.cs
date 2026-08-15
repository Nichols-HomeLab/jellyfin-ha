using System;
using System.IO;
using Emby.Server.Implementations.Library;
using MediaBrowser.Controller.Library;
using Xunit;

namespace Jellyfin.Server.Implementations.Tests.Library;

public sealed class HotCachePlaybackPathResolverTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "jellyfin-hot-cache-test-" + Guid.NewGuid().ToString("N"));

    [Fact]
    public void DirectPlay_ValidatedCopyUsesHotPathWithoutChangingCanonicalInput()
    {
        var canonical = Create("media/episode.mkv", "bytes");
        var hot = Create("hot/episode.mkv", "bytes");
        File.SetLastWriteTimeUtc(hot, new FileInfo(canonical).LastWriteTimeUtc);
        var resolver = new HotCachePlaybackPathResolver(Path.Combine(_root, "media"), Path.Combine(_root, "hot"), new NullHotCacheCoordinator());

        var result = resolver.Resolve(new PlaybackPathRequest(canonical, new FileInfo(canonical).Length, PlaybackPathPurpose.MainMedia));

        Assert.True(result.IsHot);
        Assert.Equal(hot, result.Path);
        Assert.Equal(canonical, Path.Combine(_root, "media", "episode.mkv"));
    }

    [Fact]
    public void Transcode_InvalidCopyFallsBackToCanonicalPath()
    {
        var canonical = Create("media/episode.mkv", "canonical");
        Create("hot/episode.mkv", "short");
        var resolver = new HotCachePlaybackPathResolver(Path.Combine(_root, "media"), Path.Combine(_root, "hot"), new NullHotCacheCoordinator());

        var result = resolver.Resolve(new PlaybackPathRequest(canonical, new FileInfo(canonical).Length, PlaybackPathPurpose.MainMedia));

        Assert.False(result.IsHot);
        Assert.Equal(canonical, result.Path);
        Assert.Equal("hot-length-mismatch", result.Reason);
    }

    [Fact]
    public void DirectPlay_SameLengthStaleCopyFallsBackToCanonicalPath()
    {
        var canonical = Create("media/episode.mkv", "fresh");
        var hot = Create("hot/episode.mkv", "stale");
        File.SetLastWriteTimeUtc(hot, new FileInfo(canonical).LastWriteTimeUtc.AddMinutes(-1));
        var resolver = new HotCachePlaybackPathResolver(Path.Combine(_root, "media"), Path.Combine(_root, "hot"), new NullHotCacheCoordinator());

        var result = resolver.Resolve(new PlaybackPathRequest(canonical, new FileInfo(canonical).Length, PlaybackPathPurpose.MainMedia));

        Assert.False(result.IsHot);
        Assert.Equal(canonical, result.Path);
        Assert.Equal("hot-mtime-mismatch", result.Reason);
    }

    [Fact]
    public void Transcode_SameLengthStaleCopyFallsBackToCanonicalPath()
    {
        var canonical = Create("media/episode.mkv", "fresh");
        var hot = Create("hot/episode.mkv", "stale");
        File.SetLastWriteTimeUtc(hot, new FileInfo(canonical).LastWriteTimeUtc.AddMinutes(-1));
        var resolver = new HotCachePlaybackPathResolver(Path.Combine(_root, "media"), Path.Combine(_root, "hot"), new NullHotCacheCoordinator());

        var result = resolver.Resolve(new PlaybackPathRequest(canonical, new FileInfo(canonical).Length, PlaybackPathPurpose.TranscodeInput));

        Assert.False(result.IsHot);
        Assert.Equal(canonical, result.Path);
        Assert.Equal("hot-mtime-mismatch", result.Reason);
    }

    [Fact]
    public void DirectPlay_NestedHotSymlinkFallsBackToCanonicalPath()
    {
        var canonical = Create("media/nested/episode.mkv", "bytes");
        var outside = Create("outside/episode.mkv", "bytes");
        var hotRoot = Path.Combine(_root, "hot");
        Directory.CreateDirectory(hotRoot);
        Directory.CreateSymbolicLink(Path.Combine(hotRoot, "nested"), Path.GetDirectoryName(outside)!);
        var resolver = new HotCachePlaybackPathResolver(Path.Combine(_root, "media"), hotRoot, new NullHotCacheCoordinator());

        var result = resolver.Resolve(new PlaybackPathRequest(canonical, new FileInfo(canonical).Length, PlaybackPathPurpose.MainMedia));

        Assert.False(result.IsHot);
        Assert.Equal(canonical, result.Path);
    }

    [Fact]
    public void DirectPlay_ConfiguredHotRootSymlinkFallsBackToCanonicalPath()
    {
        var canonical = Create("media/episode.mkv", "bytes");
        var outside = Path.Combine(_root, "outside");
        Directory.CreateDirectory(outside);
        var hotRoot = Path.Combine(_root, "hot-link");
        Directory.CreateSymbolicLink(hotRoot, outside);
        File.Copy(canonical, Path.Combine(outside, "episode.mkv"));
        var resolver = new HotCachePlaybackPathResolver(Path.Combine(_root, "media"), hotRoot, new NullHotCacheCoordinator());
        var result = resolver.Resolve(new PlaybackPathRequest(canonical, new FileInfo(canonical).Length, PlaybackPathPurpose.MainMedia));
        Assert.False(result.IsHot);
        Assert.Equal(canonical, result.Path);
    }

    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, true);
        }
    }

    private string Create(string relativePath, string content)
    {
        var path = Path.Combine(_root, relativePath);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, content);
        return path;
    }
}
