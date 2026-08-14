using System;

namespace MediaBrowser.Controller.Library;

/// <summary>
/// No-op catalog change notifier used by a single server instance.
/// </summary>
public sealed class NullCatalogChangeNotifier : ICatalogChangeNotifier
{
    /// <summary>
    /// Gets the shared no-op notifier.
    /// </summary>
    public static NullCatalogChangeNotifier Instance { get; } = new();

    /// <inheritdoc />
    public event Action<CatalogChange>? Changed
    {
        add { }
        remove { }
    }

    /// <inheritdoc />
    public void Publish(CatalogChange change)
    {
    }
}
