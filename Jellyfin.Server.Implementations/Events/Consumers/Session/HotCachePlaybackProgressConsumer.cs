using System.Threading;
using System.Threading.Tasks;
using MediaBrowser.Controller.Events;
using MediaBrowser.Controller.Library;

namespace Jellyfin.Server.Implementations.Events.Consumers.Session;

/// <summary>Renews a durable eviction lease while playback remains active.</summary>
public sealed class HotCachePlaybackProgressConsumer(IHotCacheCoordinator coordinator) : IEventConsumer<PlaybackProgressEventArgs>
{
    /// <inheritdoc />
    public Task OnEvent(PlaybackProgressEventArgs eventArgs) => coordinator.RecordPlaybackAsync(eventArgs, HotCachePlaybackEvent.Progressed, CancellationToken.None);
}
