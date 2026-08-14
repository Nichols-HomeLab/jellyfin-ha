using System;

namespace MediaBrowser.Controller.Library;

/// <summary>
/// No-op catalog change notifier used by a single server instance.
/// </summary>
public sealed class NullCatalogChangeNotifier : ICatalogChangeNotifier
{
    /// <inheritdoc />
    public event Action<CatalogChange>? Changed
    {
        add { }
        remove { }
    }

    /// <summary>
    /// Gets the shared no-op notifier.
    /// </summary>
    public static NullCatalogChangeNotifier Instance { get; } = new();

    /// <inheritdoc />
    public void Publish(CatalogChange change)
    {
    }
}
