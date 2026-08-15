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
