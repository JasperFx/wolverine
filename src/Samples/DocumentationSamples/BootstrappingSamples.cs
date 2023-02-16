using Microsoft.Extensions.Hosting;
using Wolverine;

namespace DocumentationSamples;

public class BootstrappingSamples
{
    
    
    public async Task set_application_assembly()
    {
        #region sample_overriding_application_assembly

        using var host = Host.CreateDefaultBuilder()
            .UseWolverine(opts =>
            {
                // Override the application assembly to help
                // Wolverine find its handlers
                // Should not be necessary in most cases
                opts.ApplicationAssembly = typeof(Program).Assembly;
            }).StartAsync();

        #endregion
    }

    public async Task adding_additional_assemblies()
    {
        #region sample_adding_extra_assemblies_to_type_discovery

        using var host = Host.CreateDefaultBuilder()
            .UseWolverine(opts =>
            {
                opts.Policies.Discovery(discovery =>
                {
                    // Add as many other assemblies as you need
                    discovery.IncludeAssembly(typeof(MessageFromOtherAssembly).Assembly);
                });
            }).StartAsync();

        #endregion
    }
}

public record MessageFromOtherAssembly;