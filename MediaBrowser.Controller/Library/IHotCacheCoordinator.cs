using System.Threading;
using System.Threading.Tasks;

namespace MediaBrowser.Controller.Library;

/// <summary>Playback lifecycle transition.</summary>
public enum HotCachePlaybackEvent
{
    /// <summary>Playback began.</summary>
    Started,

    /// <summary>Playback remains active.</summary>
    Progressed,

    /// <summary>Playback ended.</summary>
    Stopped
}

/// <summary>Coordinates durable hot-cache interest, eviction protection, and playback observations.</summary>
public interface IHotCacheCoordinator
{
    /// <summary>Records a playback lifecycle observation.</summary>
    /// <param name="playback">The Jellyfin playback event.</param>
    /// <param name="lifecycle">The lifecycle transition.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>A task that completes when durable state has changed.</returns>
    Task RecordPlaybackAsync(PlaybackProgressEventArgs playback, HotCachePlaybackEvent lifecycle, CancellationToken cancellationToken);

    /// <summary>Reconciles the included users' resume, next-up, and recently active series interests.</summary>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>A task that completes when reconciliation has finished.</returns>
    Task ReconcileAsync(CancellationToken cancellationToken);

    /// <summary>Queues a non-blocking resolver observation.</summary>
    /// <param name="request">The canonical read request.</param>
    /// <param name="resolution">The selected result.</param>
    void ObserveResolution(in PlaybackPathRequest request, in PlaybackPathResolution resolution);
}
