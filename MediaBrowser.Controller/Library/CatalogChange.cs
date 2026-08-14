using System;

namespace MediaBrowser.Controller.Library;

/// <summary>
/// Describes a committed catalog change that another server instance must observe.
/// </summary>
/// <param name="Sequence">The cluster-wide monotonically increasing sequence.</param>
/// <param name="Kind">The kind of catalog change.</param>
/// <param name="ItemId">The changed item identifier.</param>
/// <param name="ParentId">The changed item's parent identifier, when known.</param>
public readonly record struct CatalogChange(
    long Sequence,
    CatalogChangeKind Kind,
    Guid ItemId,
    Guid ParentId)
{
    /// <summary>
    /// Creates a local change. The distributed adapter assigns its sequence when publishing.
    /// </summary>
    /// <param name="kind">The kind of catalog change.</param>
    /// <param name="itemId">The changed item identifier.</param>
    /// <param name="parentId">The changed item's parent identifier, when known.</param>
    /// <returns>The unsequenced local change.</returns>
    public static CatalogChange Local(CatalogChangeKind kind, Guid itemId, Guid parentId = default)
        => new(0, kind, itemId, parentId);

    /// <summary>
    /// Creates a signal that all local catalog caches must be discarded.
    /// </summary>
    /// <param name="sequence">The latest observed cluster sequence.</param>
    /// <returns>The full-resynchronization signal.</returns>
    public static CatalogChange FullResync(long sequence)
        => new(sequence, CatalogChangeKind.FullResync, Guid.Empty, Guid.Empty);
}

/// <summary>
/// Catalog changes visible to playback replicas.
/// </summary>
public enum CatalogChangeKind
{
    /// <summary>An item was added.</summary>
    Added,

    /// <summary>An item's metadata or images changed.</summary>
    Updated,

    /// <summary>An item was removed.</summary>
    Removed,

    /// <summary>An item's media segments changed.</summary>
    MediaSegments,

    /// <summary>All local catalog caches must be discarded after a delivery gap.</summary>
    FullResync
}
