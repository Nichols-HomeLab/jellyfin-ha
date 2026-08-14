using MediaBrowser.Controller.Library;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Emby.Server.Implementations.Library;

internal static class CatalogChangeServiceCollectionExtensions
{
    public static void AddCatalogChangeNotifierFallback(this IServiceCollection services)
        => services.TryAddSingleton<ICatalogChangeNotifier>(_ => NullCatalogChangeNotifier.Instance);
}
