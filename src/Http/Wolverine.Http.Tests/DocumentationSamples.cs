using System.Reflection;
using Microsoft.Extensions.Hosting;

namespace Wolverine.Http.Tests;

public class DocumentationSamples
{
    public static async Task include_assemblies()
    {
        #region sample_programmatically_scan_assemblies

        using var host = await Host.CreateDefaultBuilder()
            .UseWolverine(opts =>
            {
                // This gives you the option to programmatically
                // add other assemblies to the discovery of HTTP endpoints
                // or message handlers
                var assembly = Assembly.Load("my other assembly name that holds HTTP endpoints or handlers");
                opts.Discovery.IncludeAssembly(assembly);
            }).StartAsync();

        #endregion
    }
}