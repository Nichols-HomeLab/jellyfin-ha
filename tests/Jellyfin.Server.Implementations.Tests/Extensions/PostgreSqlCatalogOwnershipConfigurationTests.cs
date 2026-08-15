using Jellyfin.Database.Implementations.DbConfiguration;
using Jellyfin.Server.Implementations.Extensions;
using Xunit;

namespace Jellyfin.Server.Implementations.Tests.Extensions;

/// <summary>
/// Tests PostgreSQL catalog ownership registration configuration.
/// </summary>
public sealed class PostgreSqlCatalogOwnershipConfigurationTests
{
    /// <summary>
    /// The deployed PostgreSQL plugin-provider configuration enables catalog ownership.
    /// </summary>
    [Fact]
    public void PostgreSqlPluginProvider_EnablesCatalogOwnership()
    {
        var configuration = new DatabaseConfigurationOptions
        {
            DatabaseType = "PLUGIN_PROVIDER",
            CustomProviderOptions = new CustomDatabaseOptions
            {
                PluginName = "PostgreSQL",
                PluginAssembly = "Jellyfin.Database.Providers.PostgreSQL",
                ConnectionString = "Host=database;Database=jellyfin",
            },
        };

        Assert.True(ServiceCollectionExtensions.UsesPostgreSqlCatalogOwnership(configuration));
    }
}
