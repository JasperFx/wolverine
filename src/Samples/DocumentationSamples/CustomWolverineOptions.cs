using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting;
using Wolverine;
using Wolverine.Transports.Tcp;

namespace DocumentationSamples;

public class SampleProgram1
{
    #region sample_UseWolverineWithInlineOptionsConfiguration

    public static IHostBuilder CreateHostBuilder()
    {
        return Host.CreateDefaultBuilder()

            // This adds Wolverine with inline configuration
            // of WolverineOptions
            .UseWolverine(opts =>
            {
                opts.ServiceName = "MyService";
                // Other Wolverine configuration
            });
    }

    #endregion
}

public class SampleProgram2
{
    #region sample_UseWolverineWithInlineOptionsConfigurationAndHosting

    public static IHostBuilder CreateHostBuilder()
    {
        return Host.CreateDefaultBuilder()

            // This adds Wolverine with inline configuration
            // of WolverineOptions
            .UseWolverine((context, opts) =>
            {
                // This is an example usage of the application's
                // IConfiguration inside of Wolverine bootstrapping
                var port = context.Configuration.GetValue<int>("ListenerPort");
                opts.ListenAtPort(port);

                // If we're running in development mode and you don't
                // want to worry about having all the external messaging
                // dependencies up and running, stub them out
                if (context.HostingEnvironment.IsDevelopment())
                {
                    // This will "stub" out all configured external endpoints
                    opts.StubAllExternalTransports();
                }
            });
    }

    #endregion
}