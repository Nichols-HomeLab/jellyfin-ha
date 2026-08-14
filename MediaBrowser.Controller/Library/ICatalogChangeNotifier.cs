using System;

namespace MediaBrowser.Controller.Library;

/// <summary>
/// Distributes committed catalog changes between server instances.
/// </summary>
/// <remarks>
/// Implementations must suppress messages published by the current instance and must not
/// make a successful catalog write fail when notification delivery is unavailable.
/// </remarks>
public interface ICatalogChangeNotifier
{
    /// <summary>
    /// Occurs when another server instance commits a catalog change.
    /// </summary>
    event Action<CatalogChange>? Changed;

    /// <summary>
    /// Publishes a committed local catalog change to other server instances.
    /// </summary>
    /// <param name="change">The committed change.</param>
    void Publish(CatalogChange change);
}
