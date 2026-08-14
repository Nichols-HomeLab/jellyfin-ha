using System.Threading;
using System.Threading.Tasks;
using MediaBrowser.Controller.Events;
using MediaBrowser.Controller.Library;

namespace Jellyfin.Server.Implementations.Events.Consumers.Session;

/// <summary>Releases the durable eviction lease when playback stops.</summary>
public sealed class HotCachePlaybackStopConsumer(IHotCacheCoordinator coordinator) : IEventConsumer<PlaybackStopEventArgs>
{
    /// <inheritdoc />
    public Task OnEvent(PlaybackStopEventArgs eventArgs) => coordinator.RecordPlaybackAsync(eventArgs, HotCachePlaybackEvent.Stopped, CancellationToken.None);
}
