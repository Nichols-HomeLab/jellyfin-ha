using System.Threading;
using MediaBrowser.Controller.Library;

namespace Emby.Server.Implementations.Library;

/// <summary>
/// Grants catalog ownership to a normal single-instance server.
/// </summary>
public sealed class SingleInstanceCatalogOwnership : ICatalogOwnership
{
    /// <inheritdoc />
    public bool TryGetCatalogWriteToken(out CancellationToken ownershipLost)
    {
        ownershipLost = CancellationToken.None;
        return true;
    }
}
