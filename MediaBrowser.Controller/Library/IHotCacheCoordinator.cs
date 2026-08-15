using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

#pragma warning disable SA1402 // Hot-cache administration contracts are intentionally co-located.

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

/// <summary>Administrative read and command surface backed by durable hot-cache state.</summary>
public interface IHotCacheAdministration
{
    /// <summary>Gets shared settings, backend observations, queue totals, inventory, and history.</summary>
    /// <param name="historyKind">Optional append-only history kind filter.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>The shared administrator snapshot.</returns>
    Task<HotCacheAdministrationSnapshot> GetSnapshotAsync(string? historyKind, CancellationToken cancellationToken);

    /// <summary>Validates and persists administrator settings.</summary>
    /// <param name="settings">The requested durable settings.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>A task that completes after persistence.</returns>
    Task UpdateSettingsAsync(HotCacheSettings settings, CancellationToken cancellationToken);

    /// <summary>Queues an administrator command for an existing inventory item.</summary>
    /// <param name="action">The validated administrator command.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>A task that completes after queuing.</returns>
    Task QueueActionAsync(HotCacheAction action, CancellationToken cancellationToken);
}

/// <summary>Durable administrator settings.</summary>
public sealed record HotCacheSettings(string Backend, bool Paused, double HighWatermark, double LowWatermark, int MaxLookahead = 6, long ReserveFreeBytes = 161061273600);
/// <summary>Administrator command whose item identifier is never interpreted as a path.</summary>
public sealed record HotCacheAction(string Kind, Guid? ItemId, bool ConfirmBulkEviction);
/// <summary>Shared administrator view.</summary>
public sealed record HotCacheAdministrationSnapshot(HotCacheSettings Settings, IReadOnlyList<HotCacheBackendStatus> Backends, IReadOnlyList<HotCacheQueueSummary> Queue, IReadOnlyList<HotCacheInventoryItem> Inventory, IReadOnlyList<HotCacheHistoryEntry> History);
/// <summary>Observed backend capacity and health.</summary>
public sealed record HotCacheBackendStatus(string Name, bool Mounted, bool Healthy, bool Stale, long TotalBytes, long UsedBytes, long AvailableBytes, DateTime ObservedAtUtc);
/// <summary>Queue count and byte total for a state.</summary>
public sealed record HotCacheQueueSummary(string State, long Count, long Bytes);
/// <summary>Inventory row grouped in the UI by series.</summary>
public sealed record HotCacheInventoryItem(Guid ItemId, string SeriesName, string Episode, string Reason, int InterestedUsers, int Priority, long SizeBytes, string Backend, DateTime CreatedAtUtc, DateTime UpdatedAtUtc, string State);
/// <summary>Append-only administrator history entry.</summary>
public sealed record HotCacheHistoryEntry(long Id, string Kind, string Detail, DateTime CreatedAtUtc);
