using JasperFx.Core;
using Wolverine.ComplianceTests;
using Wolverine.Tracking;
using Wolverine.Transports.Tcp;
using Wolverine.Util;
using Xunit;

namespace CoreTests.Acceptance;

public class using_ISendMyself_as_cascading_message : IntegrationContext
{
    public using_ISendMyself_as_cascading_message(DefaultApp @default) : base(@default)
    {
    }

    [Fact]
    public async Task send_myself_cascade_functionality()
    {
        var id = Guid.NewGuid();
        var session = await Host.InvokeMessageAndWaitAsync(new TriggerMessage(id));

        var cascaded = session.FindSingleTrackedMessageOfType<Cascaded>();
        cascaded.Id.ShouldBe(id);
    }

    [Fact]
    public async Task using_DelayedMessage_as_cascading()
    {
        var id = Guid.NewGuid();
        var session = await Host.InvokeMessageAndWaitAsync(new SpawnDelayResponse(id));

        var timeout = session.FindSingleTrackedMessageOfType<DelayedResponse>();
        timeout.Id.ShouldBe(id);

        var records = session.AllRecordsInOrder().ToArray();

        var envelope = session.FindEnvelopesWithMessageType<DelayedResponse>()
            .Distinct()
            .Single().Envelope;

        envelope.Status.ShouldBe(EnvelopeStatus.Scheduled);
    }

    [Fact]
    public async Task using_respond()
    {
        var receiverPort = PortFinder.GetAvailablePort();
        var senderPort = PortFinder.GetAvailablePort();

        using var sender = WolverineHost.For(opts =>
        {
            opts.PublishMessage<RequestTrigger>().ToPort(receiverPort);
            opts.ListenAtPort(senderPort);
            opts.ServiceName = "Sender";
        });

        using var receiver = WolverineHost.For(opts =>
        {
            opts.ServiceName = "Receiver";
            opts.ListenAtPort(receiverPort);
        });

        var session = await sender.TrackActivity()
            .AlsoTrack(receiver)
            .SendMessageAndWaitAsync(new RequestTrigger(58)); // RIP Derrick Thomas.

        var envelope = session.Received.SingleEnvelope<TriggeredResponse>();
        envelope.Message.ShouldBeOfType<TriggeredResponse>()
            .Number.ShouldBe(58);

        envelope.Source.ShouldBe("Receiver");
        envelope.Destination.Port.ShouldBe(senderPort);
    }
}

public record RequestTrigger(int Number);

public record TriggeredResponse(int Number);

public class TriggerHandler
{
    public static object Handle(RequestTrigger trigger)
    {
        return Respond.ToSender(new TriggeredResponse(trigger.Number));
    }

    public SelfSender Handle(TriggerMessage message)
    {
        return new SelfSender(message.Id);
    }

    public void Handle(TriggeredResponse response)
    {
    }

    public void Handle(Cascaded cascaded)
    {
        // nothing
    }

    public DelayedResponse Handle(SpawnDelayResponse message)
    {
        return new DelayedResponse(message.Id);
    }

    public void Handle(DelayedResponse response)
    {
        // Just need the handler
    }
}

public record SpawnDelayResponse(Guid Id);

public record DelayedResponse(Guid Id) : TimeoutMessage(5.Minutes());

public record TriggerMessage(Guid Id);

public record SelfSender(Guid Id) : ISendMyself
{
    public ValueTask ApplyAsync(IMessageContext context)
    {
        return context.SendAsync(new Cascaded(Id));
    }
}

public record Cascaded(Guid Id);