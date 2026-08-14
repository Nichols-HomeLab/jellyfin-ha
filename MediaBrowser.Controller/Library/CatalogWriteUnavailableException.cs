using System;

namespace MediaBrowser.Controller.Library;

/// <summary>
/// Thrown when a catalog mutation reaches an instance that does not own catalog writes.
/// </summary>
public sealed class CatalogWriteUnavailableException : InvalidOperationException
{
    /// <summary>
    /// Initializes a new instance of the <see cref="CatalogWriteUnavailableException"/> class.
    /// </summary>
    public CatalogWriteUnavailableException()
        : base("This server instance does not currently own catalog writes.")
    {
    }
}
