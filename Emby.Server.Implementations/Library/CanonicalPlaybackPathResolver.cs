using MediaBrowser.Controller.Library;

namespace Emby.Server.Implementations.Library;

/// <summary>Returns canonical storage when the disposable hot tier is not configured.</summary>
public sealed class CanonicalPlaybackPathResolver : IPlaybackPathResolver
{
    /// <inheritdoc />
    public PlaybackPathResolution Resolve(in PlaybackPathRequest request) => new(request.CanonicalPath, false, "disabled");
}
