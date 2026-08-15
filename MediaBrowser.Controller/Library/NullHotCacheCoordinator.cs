using System;
using System.Threading;
using System.Threading.Tasks;

#pragma warning disable SA1402 // Null implementations are intentionally co-located.

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

/// <summary>Cold-only administrative implementation used without PostgreSQL.</summary>
public sealed class NullHotCacheAdministration : IHotCacheAdministration
{
    private static readonly HotCacheAdministrationSnapshot Empty = new(new HotCacheSettings("unraid-temp", false, .90, .75), [], [], [], []);

    /// <inheritdoc />
    public Task<HotCacheAdministrationSnapshot> GetSnapshotAsync(string? historyKind, CancellationToken cancellationToken) => Task.FromResult(Empty);

    /// <inheritdoc />
    public Task UpdateSettingsAsync(HotCacheSettings settings, CancellationToken cancellationToken) => Task.CompletedTask;

    /// <inheritdoc />
    public Task QueueActionAsync(HotCacheAction action, CancellationToken cancellationToken) => Task.CompletedTask;
}
