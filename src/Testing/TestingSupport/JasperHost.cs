using BaselineTypeDiscovery;
using System;
using System.Threading.Tasks;
using Wolverine;
using Microsoft.Extensions.Hosting;
using Oakton;
using Oakton.Resources;

[assembly: IgnoreAssembly]

namespace TestingSupport
{
    /// <summary>
    ///     Shortcut to bootstrap simple Wolverine applications.
    ///     Syntactical sugar over Host.CreateDefaultBuilder().UseWolverine().RunOaktonCommands(args);
    /// </summary>
    public static class WolverineHost
    {
        /// <summary>
        ///     Creates a Wolverine application for the current executing assembly
        ///     using all the default Wolverine configurations
        /// </summary>
        /// <returns></returns>
        public static IHost Basic()
        {
            return bootstrap(new WolverineOptions());
        }

        /// <summary>
        ///     Builds and initializes a IHost for the options
        /// </summary>
        /// <param name="options"></param>
        /// <returns></returns>
        public static IHost For(WolverineOptions options)
        {
            return bootstrap(options);
        }

        /// <summary>
        ///     Builds and initializes a IHost for the configured WolverineOptions
        /// </summary>
        /// <param name="configure"></param>
        /// <returns></returns>
        public static IHost For(Action<WolverineOptions> configure)
        {
            var registry = new WolverineOptions();
            configure(registry);
            return bootstrap(registry);
        }

        private static IHost bootstrap(WolverineOptions options)
        {
            return Host.CreateDefaultBuilder()
                .UseWolverine(options, (c,o) => {})
                .UseResourceSetupOnStartup(StartupAction.ResetState)
                //.ConfigureLogging(x => x.ClearProviders())
                .Start();
        }


        /// <summary>
        ///     Shortcut to create a new empty WebHostBuilder with Wolverine's default
        ///     settings, add Wolverine with the supplied configuration, and bootstrap the application
        ///     from the command line
        /// </summary>
        /// <param name="args"></param>
        /// <param name="configure"></param>
        /// <returns></returns>
        public static Task<int> Run(string[] args, Action<HostBuilderContext, WolverineOptions> configure)
        {
            return Host.CreateDefaultBuilder().UseWolverine(configure).RunOaktonCommands(args);
        }
    }
}
