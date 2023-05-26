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
            }).StartAsync();

        #endregion
    }
}

public interface IFoo
{
}

public class Foo : IFoo
{
}