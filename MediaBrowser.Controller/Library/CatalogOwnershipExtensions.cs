using System.Threading;

namespace MediaBrowser.Controller.Library;

/// <summary>
/// Creates fail-closed cancellation scopes for catalog mutations.
/// </summary>
public static class CatalogOwnershipExtensions
{
    /// <summary>
    /// Creates a token source cancelled by either the caller or loss of catalog ownership.
    /// </summary>
    /// <param name="ownership">The catalog ownership authority.</param>
    /// <param name="cancellationToken">The caller cancellation token.</param>
    /// <returns>A linked cancellation source for the catalog mutation.</returns>
    /// <exception cref="CatalogWriteUnavailableException">The instance is not the catalog owner.</exception>
    public static CancellationTokenSource CreateCatalogWriteCancellationSource(
        this ICatalogOwnership ownership,
        CancellationToken cancellationToken)
    {
        if (!ownership.TryGetCatalogWriteToken(out var ownershipLost))
        {
            throw new CatalogWriteUnavailableException();
        }

        ownershipLost.ThrowIfCancellationRequested();
        return CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, ownershipLost);
    }

    /// <summary>
    /// Verifies that this instance currently owns catalog writes.
    /// </summary>
    /// <param name="ownership">The catalog ownership authority.</param>
    /// <returns>A token cancelled if ownership is subsequently lost.</returns>
    /// <exception cref="CatalogWriteUnavailableException">The instance is not the catalog owner.</exception>
    public static CancellationToken RequireCatalogWriteToken(this ICatalogOwnership ownership)
    {
        if (!ownership.TryGetCatalogWriteToken(out var ownershipLost))
        {
            throw new CatalogWriteUnavailableException();
        }

        ownershipLost.ThrowIfCancellationRequested();
        return ownershipLost;
    }
}
