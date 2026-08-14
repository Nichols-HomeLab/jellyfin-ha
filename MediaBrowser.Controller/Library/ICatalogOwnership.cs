using System.Threading;

namespace MediaBrowser.Controller.Library;

/// <summary>
/// Provides cluster-wide ownership of catalog mutations.
/// </summary>
public interface ICatalogOwnership
{
    /// <summary>
    /// Tries to obtain the current catalog-writer ownership token.
    /// </summary>
    /// <param name="ownershipLost">A token that is cancelled when this instance loses ownership.</param>
    /// <returns><see langword="true"/> when this instance currently owns catalog mutations.</returns>
    bool TryGetCatalogWriteToken(out CancellationToken ownershipLost);
}
