using System.Threading;
using System.Threading.Tasks;

namespace MediaBrowser.Controller.Library;

/// <summary>Cold-only coordinator used when the PostgreSQL hot-cache feature is unavailable.</summary>
public sealed class NullHotCacheCoordinator : IHotCacheCoordinator
{
    /// <inheritdoc />
    public Task RecordPlaybackAsync(PlaybackProgressEventArgs playback, HotCachePlaybackEvent lifecycle, CancellationToken cancellationToken) => Task.CompletedTask;

    /// <inheritdoc />
    public Task ReconcileAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    /// <inheritdoc />
    public void ObserveResolution(in PlaybackPathRequest request, in PlaybackPathResolution resolution)
    {
    }
}
