using Microsoft.Extensions.DependencyInjection;
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

public interface IFoo
{
}

public class Foo : IFoo
{
}