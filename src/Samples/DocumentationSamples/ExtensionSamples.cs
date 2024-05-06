using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Newtonsoft.Json;
using Wolverine;

namespace DocumentationSamples;

#region sample_SampleExtension

public class SampleExtension : IWolverineExtension
{
    public void Configure(WolverineOptions options)
    {
        // Add service registrations
        options.Services.AddTransient<IFoo, Foo>();

        // Alter settings within the application
        options
            .UseNewtonsoftForSerialization(settings => settings.TypeNameHandling = TypeNameHandling.None);
    }
}

#endregion

#region sample_configuration_using_extension

public class ConfigurationUsingExtension : IWolverineExtension
{
    private readonly IConfiguration _configuration;

    // Use constructor injection from your DI container at runtime
    public ConfigurationUsingExtension(IConfiguration configuration)
    {
        _configuration = configuration;
    }

    public void Configure(WolverineOptions options)
    {
        // Configure the wolverine application using 
        // the information from IConfiguration
    }
}

#endregion


public static class ExtensionUse
{
    public static async Task spin_up()
    {
        #region sample_including_extension

        using var host = await Host.CreateDefaultBuilder()
            .UseWolverine(opts =>
            {
                // Including a single extension
                opts.Include<SampleExtension>();
                
                // Or add a Wolverine extension that needs
                // to use IoC services
                opts.Services.AddWolverineExtension<ConfigurationUsingExtension>();

            })
            
            .ConfigureServices(services =>
            {
                // This is the same logical usage, just showing that it
                // can be done directly against IServiceCollection
                services.AddWolverineExtension<ConfigurationUsingExtension>();
            })
            
            .StartAsync();

        #endregion
    }
}

public interface IFoo;

public class Foo : IFoo;