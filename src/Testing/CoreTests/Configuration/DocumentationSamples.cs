using Lamar;
using Lamar.Microsoft.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace CoreTests.Configuration;

public static class DocumentationSamples
{
    public static async Task bootstrap_with_lamar()
    {
        #region sample_use_lamar_with_host_builder

        // With IHostBuilder
        var builder = Host.CreateDefaultBuilder();
        builder.UseLamar();

        #endregion
        
        
    }

    public static async Task bootstrap_with_lamar_using_web_app()
    {
        var builder = Host.CreateApplicationBuilder();
        
        // Little ugly, and Lamar *should* have a helper for this...
        builder.ConfigureContainer<ServiceRegistry>(new LamarServiceProviderFactory());
    }
}