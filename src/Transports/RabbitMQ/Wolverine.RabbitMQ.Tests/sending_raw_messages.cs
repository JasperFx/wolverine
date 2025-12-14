using System.Diagnostics;
using System.Text;
using Marten.Services;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting;
using Shouldly;
using Wolverine.ComplianceTests;
using Wolverine.Tracking;
using Xunit;

namespace Wolverine.RabbitMQ.Tests;

public class sending_raw_messages
{
    [Fact]
    public async Task send_end_to_end_with_default_message_type_name()
    {
        var theQueueName = RabbitTesting.NextQueueName();
        using var publisher = await Host.CreateDefaultBuilder()
            .UseWolverine(opts =>
            {
                opts.UseRabbitMq().DisableDeadLetterQueueing().AutoProvision().AutoPurgeOnStartup();

                opts.PublishAllMessages()
                    .ToRabbitQueue(theQueueName).SendInline();
            }).StartAsync();


        using var receiver = await WolverineHost.ForAsync(opts =>
        {
            opts.UseRabbitMq().AutoProvision().DisableDeadLetterQueueing();

            opts.ListenToRabbitQueue(theQueueName).DefaultIncomingMessage<RawMessage>()
                
                .DefaultIncomingMessage<RawMessage>()
                .PreFetchCount(10).ProcessInline();
        });

        var messageData = receiver.GetRuntime().Options.DefaultSerializer
            .Write(new Envelope() { Message = new RawMessage("Kareem Hunt") });


        var tracked = await publisher.TrackActivity()
            .AlsoTrack(receiver)
            .IncludeExternalTransports()
            .ExecuteAndWaitAsync(c => c.EndpointFor(theQueueName).SendRawMessageAsync(messageData, typeof(RawMessage)));

        var received = tracked.Received.SingleEnvelope<RawMessage>();
        received.Message.ShouldBeOfType<RawMessage>().Name.ShouldBe("Kareem Hunt");
    }
    
    [Fact]
    public async Task send_end_to_end_with_supplied_message_type_name()
    {
        var theQueueName = RabbitTesting.NextQueueName();
        using var publisher = await Host.CreateDefaultBuilder()
            .UseWolverine(opts =>
            {
                opts.UseRabbitMq().DisableDeadLetterQueueing().AutoProvision().AutoPurgeOnStartup();

                opts.PublishAllMessages()
                    .ToRabbitQueue(theQueueName).SendInline();
            }).StartAsync();


        using var receiver = await WolverineHost.ForAsync(opts =>
        {
            opts.UseRabbitMq().AutoProvision().DisableDeadLetterQueueing();

            opts.ListenToRabbitQueue(theQueueName)
                .PreFetchCount(10).ProcessInline();
        });

        var messageData = receiver.GetRuntime().Options.DefaultSerializer
            .Write(new Envelope() { Message = new RawMessage("Nohl Williams") });


        var tracked = await publisher.TrackActivity()
            .AlsoTrack(receiver)
            .IncludeExternalTransports()
            .ExecuteAndWaitAsync(c => c.EndpointFor(theQueueName).SendRawMessageAsync(messageData, typeof(RawMessage)));

        var received = tracked.Received.SingleEnvelope<RawMessage>();
        received.Message.ShouldBeOfType<RawMessage>().Name.ShouldBe("Nohl Williams");
    }
    
    [Fact]
    public async Task send_end_to_end_customize_envelope()
    {
        var theQueueName = RabbitTesting.NextQueueName();
        using var publisher = await Host.CreateDefaultBuilder()
            .UseWolverine(opts =>
            {
                opts.UseRabbitMq().DisableDeadLetterQueueing().AutoProvision().AutoPurgeOnStartup();

                opts.PublishAllMessages()
                    .ToRabbitQueue(theQueueName).SendInline();
            }).StartAsync();


        using var receiver = await WolverineHost.ForAsync(opts =>
        {
            opts.UseRabbitMq().AutoProvision().DisableDeadLetterQueueing();

            opts.ListenToRabbitQueue(theQueueName)
                .PreFetchCount(10).ProcessInline();
        });

        var messageData = receiver.GetRuntime().Options.DefaultSerializer
            .Write(new Envelope() { Message = new RawMessage("Nohl Williams") });


        var tracked = await publisher.TrackActivity()
            .AlsoTrack(receiver)
            .IncludeExternalTransports()
            .ExecuteAndWaitAsync(c =>
            {
                return c
                    .EndpointFor(theQueueName)
                    .SendRawMessageAsync(messageData, configure: e =>
                    {
                        e.SetMessageType<RawMessage>();
                        e.Headers["name"] = "Chris Jones";
                    });
            });

        var received = tracked.Received.SingleEnvelope<RawMessage>();
        received.Message.ShouldBeOfType<RawMessage>().Name.ShouldBe("Nohl Williams");
        received.Headers["name"].ShouldBe("Chris Jones");
    }


    public static async Task send_messages_with_raw_data()
    {
        #region sample_simple_rabbit_mq_setup_for_raw_messages

        var builder = Host.CreateApplicationBuilder();
        var connectionString = builder.Configuration.GetConnectionString("rabbit");

        builder.UseWolverine(opts =>
        {
            opts.UseRabbitMq(connectionString).AutoProvision();

            opts.ListenToRabbitQueue("batches")

                // Pay attention to this. This helps Wolverine
                // "know" that if the message type isn't specified
                // on the incoming Rabbit MQ message to assume that
                // the .NET message type is RunBatch
                .DefaultIncomingMessage<RunBatch>()
                
                // The default endpoint name would be "batches" anyway, but still
                // good to show this if you want to use more readable names:
                .Named("batches");

            opts.ListenToRabbitQueue("control");
        });

        using var host = builder.Build();
        await host.StartAsync();

        #endregion

        #region sample_context_for_raw_message_sending

        // Helper method for testing in Wolverine that
        // gives you a new IMessageBus instance without having to 
        // muck around with scoped service providers
        IMessageBus bus = host.MessageBus();
        
        // The raw message data, but pretend this was sourced from a database
        // table or some other non-Wolverine storage in your system
        byte[] messageData 
            = Encoding.Default.GetBytes("{\"Name\": \"George Karlaftis\"}");

            #endregion


            #region sample_simple_usage_of_sending_by_raw_data

            // Simplest possible usage. This can work because the
            // listening endpoint has a configured default message
            // type
            await bus
                
                // choosing the destination endpoint by its name
                // Rabbit MQ queues use the queue name by default
                .EndpointFor("batches")
                .SendRawMessageAsync(messageData);

            // Same usage, but locate by the Wolverine Uri
            await bus
                
                // choosing the destination endpoint by its name
                // Rabbit MQ queues use the queue name by default
                .EndpointFor(new Uri("rabbitmq://queue/batches"))
                .SendRawMessageAsync(messageData);

            #endregion

            #region sample_more_advanced_usage_of_raw_message_sending

            await bus
                .EndpointFor(new Uri("rabbitmq://queue/control"))
                
                // In this case I helped Wolverine out by telling it
                // what the .NET message type is for this message
                .SendRawMessageAsync(messageData, typeof(RunBatch));

            await bus
                .EndpointFor(new Uri("rabbitmq://queue/control"))
                
                // In this case I helped Wolverine out by telling it
                // what the .NET message type is for this message
                .SendRawMessageAsync(messageData, configure: env =>
                {
                    
                    // Alternative usage to just work directly
                    // with Wolverine's Envelope wrapper
                    env.SetMessageType<RunBatch>();

                    // And you can do *anything* with message metadata
                    // using the Envelope wrapper
                    // Use a little bit of caution with this though
                    env.Headers["user"] = "jack";
                });

            #endregion

    }
}

public record RunBatch(string Name);

public record RawMessage(string Name);

public static class RawMessageHandler
{
    public static void Handle(RawMessage m) => Debug.WriteLine("Got raw message " + m.Name);
}