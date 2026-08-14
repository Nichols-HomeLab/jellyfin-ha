using System.Threading;
using System.Threading.Tasks;
using MediaBrowser.Controller.Events;
using MediaBrowser.Controller.Library;

namespace Jellyfin.Server.Implementations.Events.Consumers.Session;

/// <summary>Creates a durable eviction lease when playback starts.</summary>
public sealed class HotCachePlaybackStartConsumer(IHotCacheCoordinator coordinator) : IEventConsumer<PlaybackStartEventArgs>
{
    /// <inheritdoc />
    public Task OnEvent(PlaybackStartEventArgs eventArgs) => coordinator.RecordPlaybackAsync(eventArgs, HotCachePlaybackEvent.Started, CancellationToken.None);
}
