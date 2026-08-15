using System.IO;
using Xunit;

namespace Jellyfin.Server.Implementations.Tests.Library;

/// <summary>Protects the two serving seams that may substitute a validated hot-cache path.</summary>
public sealed class HotCachePlaybackConsumerContractTests
{
    [Fact]
    public void DirectPlayMediaSourceUsesMainMediaResolution()
    {
        var source = Read("Emby.Server.Implementations/Library/MediaSourceManager.cs");

        Contains("new PlaybackPathRequest(canonicalPath, source.Size, PlaybackPathPurpose.MainMedia)", source);
        Contains("source.Path = resolution.Path", source);
    }

    [Fact]
    public void TranscodeProbeUsesTranscodeResolutionAndPreservesColdFallback()
    {
        var source = Read("MediaBrowser.MediaEncoding/Encoder/MediaEncoder.cs");

        Contains("new PlaybackPathRequest(canonicalPath, request.MediaSource.Size, PlaybackPathPurpose.TranscodeInput)", source);
        Contains("?? canonicalPath", source);
    }

    private static string Read(string relativePath)
    {
        var directory = AppContext.BaseDirectory;
        while (directory is not null)
        {
            var candidate = Path.Combine(directory, relativePath);
            if (File.Exists(candidate))
            {
                return File.ReadAllText(candidate);
            }

            directory = Directory.GetParent(directory)?.FullName;
        }

        throw new DirectoryNotFoundException($"Repository source was not found for {relativePath}.");
    }

    private static void Contains(string expected, string actual)
        => Assert.Contains(expected, actual, StringComparison.Ordinal);
}
