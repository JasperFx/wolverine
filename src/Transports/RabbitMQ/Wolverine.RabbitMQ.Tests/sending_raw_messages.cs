using System.Diagnostics;
using Marten.Services;
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
}

public record RawMessage(string Name);

public static class RawMessageHandler
{
    public static void Handle(RawMessage m) => Debug.WriteLine("Got raw message " + m.Name);
}