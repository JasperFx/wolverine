using System.Diagnostics;
using IntegrationTests;
using JasperFx.Core;
using Microsoft.Extensions.Hosting;
using Shouldly;
using Wolverine;
using Wolverine.Postgresql;
using Wolverine.Runtime.RemoteInvocation;
using Wolverine.Tracking;
using Wolverine.Transports.Tcp;
using Wolverine.Util;
using Xunit;

namespace SlowTests;

public class tracked_session_mechanics
{
    [Fact]
    public async Task failure_acks_show_up_in_tracked_session()
    {
        var port1 = PortFinder.GetAvailablePort();
        var port2 = PortFinder.GetAvailablePort();

        using var sender = await Host.CreateDefaultBuilder()
            .UseWolverine(opts =>
            {
                opts.Discovery.DisableConventionalDiscovery();
                opts.PublishAllMessages().ToPort(port2);
                opts.ListenAtPort(port1);
            }).StartAsync();
        
        using var receiver = await Host.CreateDefaultBuilder()
            .UseWolverine(opts =>
            {
                opts.ListenAtPort(port2);
            }).StartAsync();

        await Should.ThrowAsync<WolverineRequestReplyException>(async () =>
        {
            var (tracked, response) = await sender.TrackActivity().IncludeExternalTransports().AlsoTrack(receiver)
                .InvokeAndWaitAsync<ResponseForRequest>(new RequestResponse(false, "Something"));
        });


    }

    [Fact]
    public async Task deal_with_in_memory_scheduled_message()
    {
        using var host = await Host.CreateDefaultBuilder()
            .UseWolverine(opts =>
            {

            }).StartAsync();

        // Should finish cleanly
        var tracked = await host.SendMessageAndWaitAsync(new TriggerScheduledMessage("Chiefs"));

        var records = tracked.AllRecordsInOrder().ToArray();
        
        tracked.Sent.SingleMessage<ScheduledMessage>()
            .Text.ShouldBe("Chiefs");
        
        tracked.Scheduled.SingleMessage<ScheduledMessage>()
            .Text.ShouldBe("Chiefs");
    }

    [Fact]
    public async Task deal_with_locally_scheduled_execution()
    {
        #region sample_dealing_with_locally_scheduled_messages

        // In this case we're just executing everything in memory
        using var host = await Host.CreateDefaultBuilder()
            .UseWolverine(opts =>
            {
                opts.PersistMessagesWithPostgresql(Servers.PostgresConnectionString, "wolverine");
                opts.Policies.UseDurableInboxOnAllListeners();
            }).StartAsync();

        // Should finish cleanly
        var tracked = await host.SendMessageAndWaitAsync(new TriggerScheduledMessage("Chiefs"));
        
        // Here's how you can query against the messages that were detected to be scheduled
        tracked.Scheduled.SingleMessage<ScheduledMessage>()
            .Text.ShouldBe("Chiefs");

        // This API will try to immediately play any scheduled messages immediately
        var replayed = await tracked.PlayScheduledMessagesAsync(10.Seconds());
        replayed.Executed.SingleMessage<ScheduledMessage>().Text.ShouldBe("Chiefs");

        #endregion
    }

    [Fact]
    public async Task handle_scheduled_delivery_to_external_transport()
    {
        #region sample_handling_scheduled_delivery_to_external_transport

        var port1 = PortFinder.GetAvailablePort();
        var port2 = PortFinder.GetAvailablePort();

        using var sender = await Host.CreateDefaultBuilder()
            .UseWolverine(opts =>
            {
                opts.PublishMessage<ScheduledMessage>().ToPort(port2);
                opts.ListenAtPort(port1);
            }).StartAsync();
        
        using var receiver = await Host.CreateDefaultBuilder()
            .UseWolverine(opts =>
            {
                opts.ListenAtPort(port2);
            }).StartAsync();
        
        // Should finish cleanly
        var tracked = await sender
            .TrackActivity()
            .IncludeExternalTransports()
            .AlsoTrack(receiver)
            .InvokeMessageAndWaitAsync(new TriggerScheduledMessage("Broncos"));

        tracked.Scheduled.SingleMessage<ScheduledMessage>()
            .Text.ShouldBe("Broncos");
        
        var replayed = await tracked.PlayScheduledMessagesAsync(10.Seconds());
        replayed.Executed.SingleMessage<ScheduledMessage>().Text.ShouldBe("Broncos");

        #endregion
    }
}

public record TriggerScheduledMessage(string Text);

public record ScheduledMessage(string Text);

public record TriggerResponse(bool WillReturn, string Text);

public record RequestResponse(bool WillReturn, string Text);

public record ResponseForRequest(string Text);

public static class RequestResponseHandler
{
    public static async Task HandleAsync(TriggerResponse message, IMessageBus bus)
    {
        var final = await bus.InvokeAsync<ResponseForRequest>(new RequestResponse(message.WillReturn, message.Text));
        final.ShouldNotBeNull();
    }
    
    public static ResponseForRequest? Handle(RequestResponse msg) => msg.WillReturn ? new(msg.Text) : null;

    #region sample_handlers_for_trigger_scheduled_message

    public static DeliveryMessage<ScheduledMessage> Handle(TriggerScheduledMessage message)
    {
        // This causes a message to be scheduled for delivery in 5 minutes from now
        return new ScheduledMessage(message.Text).DelayedFor(5.Minutes());
    }

    public static void Handle(ScheduledMessage message) => Debug.WriteLine("Got scheduled message");

    #endregion
}