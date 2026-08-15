using System;
using System.IO;
using Emby.Server.Implementations.Library;
using MediaBrowser.Controller.Configuration;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.MediaEncoding;
using MediaBrowser.MediaEncoding.Encoder;
using MediaBrowser.Model.Dto;
using MediaBrowser.Model.Entities;
using MediaBrowser.Model.Globalization;
using MediaBrowser.Model.IO;
using MediaBrowser.Model.MediaInfo;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Moq;
using Xunit;

namespace Jellyfin.Server.Implementations.Tests.Library;

public sealed class HotCacheMediaEncoderTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "hot-cache-encoder-" + Guid.NewGuid().ToString("N"));

    [Fact]
    public void FfmpegInputRevalidatesTransientHotPathAndFallsCold()
    {
        var canonicalRoot = Path.Combine(_root, "media");
        var hotRoot = Path.Combine(_root, "hot");
        Directory.CreateDirectory(canonicalRoot);
        Directory.CreateDirectory(hotRoot);
        var canonical = Path.Combine(canonicalRoot, "episode.mkv");
        var hot = Path.Combine(hotRoot, "episode.mkv");
        File.WriteAllText(canonical, "bytes");
        File.Copy(canonical, hot);
        File.SetLastWriteTimeUtc(hot, File.GetLastWriteTimeUtc(canonical));
        var encoder = new MediaEncoder(Mock.Of<ILogger<MediaEncoder>>(), Mock.Of<IServerConfigurationManager>(), Mock.Of<IFileSystem>(), Mock.Of<IBlurayExaminer>(), Mock.Of<ILocalizationManager>(), new ConfigurationBuilder().Build(), Mock.Of<IServerConfigurationManager>(), new HotCachePlaybackPathResolver(canonicalRoot, hotRoot, new NullHotCacheCoordinator()));
        var state = new EncodingJobInfo(TranscodingJobType.Progressive) { MediaPath = hot, MediaSource = new MediaSourceInfo { Path = hot, CanonicalPath = canonical, Size = new FileInfo(canonical).Length, Protocol = MediaProtocol.File, VideoType = VideoType.VideoFile } };

        Assert.Contains(hot, encoder.GetInputPathArgument(state), StringComparison.Ordinal);
        File.Delete(hot);
        Assert.Contains(canonical, encoder.GetInputPathArgument(state), StringComparison.Ordinal);
    }

    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, true);
        }
    }
}
