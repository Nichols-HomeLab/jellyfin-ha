using Microsoft.Extensions.Configuration;
using Xunit;

namespace Jellyfin.HotCache.Worker.Tests;

public sealed class HotCacheConfigurationTests
{
    [Fact]
    public void KubernetesDoubleUnderscoreEnvironmentVariablesBindToWorkerConfigurationPaths()
    {
        const string variable = "Jellyfin__HotCache__CanonicalRoot";
        var original = Environment.GetEnvironmentVariable(variable);
        try
        {
            Environment.SetEnvironmentVariable(variable, "/media");

            var configuration = new ConfigurationBuilder().AddEnvironmentVariables().Build();

            Assert.Equal("/media", configuration["Jellyfin:HotCache:CanonicalRoot"]);
        }
        finally
        {
            Environment.SetEnvironmentVariable(variable, original);
        }
    }
}
