using Wolverine;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace DocumentationSamples
{
    public static class wolverine_app_services
    {
        public static async Task WolverineAppWithServices()
        {
            #region sample_WolverineAppWithServices

            using var host = Host.CreateDefaultBuilder()
                .UseWolverine(opts =>
                {
                    // Add service registrations with the ASP.Net Core
                    // DI abstractions
                    opts.Services.AddLogging();

                    // or mix and match with StructureMap style
                    // registrations
                    opts.Services.For(typeof(ILogger)).Use(typeof(Logger<>));
                }).StartAsync();

            #endregion
        }
    }


    public interface IThirdPartyService
    {
    }

    public class StubThirdPartyService : IThirdPartyService
    {
    }
}
