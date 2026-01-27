using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Wolverine;
using Wolverine.Runtime;

namespace DocumentationSamples;

public record PostInSlack(string Room, string Message);

#region sample_MyCustomActionForMissingHandlers

public class MyCustomActionForMissingHandlers : IMissingHandler
{
    public ValueTask HandleAsync(IEnvelopeLifecycle context, IWolverineRuntime root)
    {
        var bus = new MessageBus(root);
        return bus.PublishAsync(new PostInSlack("Incidents",
            $"Got an unknown message with type '{context.Envelope.MessageType}' and id {context.Envelope.Id}"));
    }
}

#endregion

public static class ConfigureMissingHandlers
{
    public static async Task configure()
    {
        #region sample_registering_custom_missing_handler

        var builder = Host.CreateApplicationBuilder();
        builder.UseWolverine(opts =>
        {
            // configuration
            opts.UnknownMessageBehavior = UnknownMessageBehavior.DeadLetterQueue;
        });

        builder.Services.AddSingleton<IMissingHandler, MyCustomActionForMissingHandlers>();

        #endregion
    }
}

