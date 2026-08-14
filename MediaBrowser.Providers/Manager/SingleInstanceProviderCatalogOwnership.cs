using System.Threading;
using MediaBrowser.Controller.Library;

namespace MediaBrowser.Providers.Manager;

/// <summary>
/// Preserves upstream image behavior when providers are constructed outside dependency injection.
/// </summary>
internal sealed class SingleInstanceProviderCatalogOwnership : ICatalogOwnership
{
    /// <inheritdoc />
    public bool TryGetCatalogWriteToken(out CancellationToken ownershipLost)
    {
        ownershipLost = CancellationToken.None;
        return true;
    }
}
