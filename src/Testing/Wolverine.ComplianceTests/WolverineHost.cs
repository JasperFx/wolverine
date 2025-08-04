using JasperFx.Core.TypeScanning;
using Microsoft.Extensions.Hosting;
using JasperFx;
using JasperFx.Resources;
using Wolverine;

[assembly: IgnoreAssembly]

namespace Wolverine.ComplianceTests;

/// <summary>
///     Shortcut to bootstrap simple Wolverine applications.
///     Syntactical sugar over Host.CreateDefaultBuilder().UseWolverine().RunJasperFxCommands(args);
/// </summary>
[Obsolete("This should have gone away a long time ago")]
public static class WolverineHost
{
    /// <summary>
    ///     Creates a Wolverine application for the current executing assembly
    ///     using all the default Wolverine configurations
    /// </summary>
    /// <returns></returns>
    public static IHost Basic()
    {
        return bootstrap(_ => {});
    }

    [Obsolete("Try to eliminate this")]
    public static IHost For(Action<WolverineOptions> configure)
    {
        return bootstrap(configure);
    }

    public static Task<IHost> ForAsync(Action<WolverineOptions> configure)
    {
        return bootstrapAsync(configure);
    }

    private static IHost bootstrap(Action<WolverineOptions> configure)
    {
        return Host.CreateDefaultBuilder()
            .UseWolverine(configure)
            .UseResourceSetupOnStartup(StartupAction.ResetState)
            //.ConfigureLogging(x => x.ClearProviders())
            .Start();
    }

    private static Task<IHost> bootstrapAsync(Action<WolverineOptions> configure)
    {
        return Host.CreateDefaultBuilder()
            .UseWolverine(configure)
            .UseResourceSetupOnStartup(StartupAction.ResetState)
            //.ConfigureLogging(x => x.ClearProviders())
            .StartAsync();
    }
}