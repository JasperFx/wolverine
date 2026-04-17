using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Wolverine;
using Wolverine.Runtime;

namespace DocumentationSamples;

public static class MissingHandlerSamples
{
    public static async Task ConfigureMissingHandler()
    {
        #region sample_configuremissinghandler
        using var host = await Host.CreateDefaultBuilder()
            .UseWolverine(opts =>
            {
                // Just add your type to the IoC container
                opts.Services.AddSingleton<IMissingHandler, MyMissingHandler>();
            }).StartAsync();

        #endregion
    }
}

#region sample_mymissinghandler
public class MyMissingHandler : IMissingHandler
{
    public ValueTask HandleAsync(IEnvelopeLifecycle context, IWolverineRuntime root)
    {
        return context
            .SendFailureAcknowledgementAsync("I don't know how to process this message");
    }
}

#endregion