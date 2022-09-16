using Wolverine;
using Wolverine.Runtime;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace DocumentationSamples
{
    public static class MissingHandlerSamples
    {
        public static async Task ConfigureMissingHandler()
        {
            #region sample_ConfigureMissingHandler

            using var host = await Host.CreateDefaultBuilder()
                .UseWolverine(opts =>
                {
                    // Just add your type to the IoC container
                    opts.Services.AddSingleton<IMissingHandler, MyMissingHandler>();
                }).StartAsync();

            #endregion
        }
    }

    #region sample_MyMissingHandler

    public class MyMissingHandler : IMissingHandler
    {
        public ValueTask HandleAsync(IMessageContext context, IWolverineRuntime root)
        {
            return context
                .SendFailureAcknowledgementAsync("I don't know how to process this message");
        }
    }

    #endregion
}
