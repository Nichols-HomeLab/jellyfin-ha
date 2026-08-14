namespace MediaBrowser.Controller.Library;

/// <summary>Identifies the server-side reader requesting a path.</summary>
public enum PlaybackPathPurpose
{
    /// <summary>The primary direct-play or transcoding input.</summary>
    MainMedia,

    /// <summary>An external audio or subtitle stream.</summary>
    ExternalStream,

    /// <summary>An FFprobe input.</summary>
    Probe
}

/// <summary>Chooses the bytes for one server-side media read without changing library identity.</summary>
public interface IPlaybackPathResolver
{
    /// <summary>Returns a validated local path, falling back to canonical storage on every miss.</summary>
    /// <param name="request">The canonical read request.</param>
    /// <returns>The selected local path.</returns>
    PlaybackPathResolution Resolve(in PlaybackPathRequest request);
}

/// <summary>Describes a server-side media read.</summary>
public readonly record struct PlaybackPathRequest(string CanonicalPath, long? ExpectedLength, PlaybackPathPurpose Purpose);

/// <summary>Contains the selected local path and a bounded diagnostic outcome.</summary>
public readonly record struct PlaybackPathResolution(string Path, bool IsHot, string Reason);
