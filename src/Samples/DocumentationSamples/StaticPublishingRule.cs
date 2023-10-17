using Microsoft.Extensions.Hosting;
using TestingSupport.Compliance;
using TestMessages;
using Wolverine;
using Wolverine.Transports.Tcp;

namespace DocumentationSamples;

public static class static_publishing_rules
{
    public static async Task StaticPublishingRules()
    {
        #region sample_StaticPublishingRules

        using var host = Host.CreateDefaultBuilder()
            .UseWolverine(opts =>
            {
                // Route a single message type
                opts.PublishMessage<PingMessage>()
                    .ToServerAndPort("server", 1111);

                // Send every possible message to a TCP listener
                // on this box at port 2222
                opts.PublishAllMessages().ToPort(2222);

                // Or use a more fluent interface style
                opts.Publish().MessagesFromAssembly(typeof(PingMessage).Assembly)
                    .ToPort(3333);

                // Complicated rules, I don't think folks will use this much
                opts.Publish(rule =>
                {
                    // Apply as many message matching
                    // rules as you need

                    // Specific message types
                    rule.Message<PingMessage>();
                    rule.Message<Message1>();

                    // Implementing a specific marker interface or common base class
                    rule.MessagesImplementing<IEventMarker>();

                    // All types in a certain assembly
                    rule.MessagesFromAssemblyContaining<PingMessage>();

                    // or this
                    rule.MessagesFromAssembly(typeof(PingMessage).Assembly);

                    // or by namespace
                    rule.MessagesFromNamespace("MyMessageLibrary");
                    rule.MessagesFromNamespaceContaining<PingMessage>();

                    // Express the subscribers
                    rule.ToPort(1111);
                    rule.ToPort(2222);
                });

                // Or you just send all messages to a certain endpoint
                opts.PublishAllMessages().ToPort(3333);
            }).StartAsync();

        #endregion
    }
}

public interface IEventMarker
{    
}