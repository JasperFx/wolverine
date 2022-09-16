using System;
using System.Linq;
using System.Threading.Tasks;
using Baseline.Dates;
using Shouldly;
using Wolverine;
using Wolverine.Tracking;
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

        var envelope = session.FindEnvelopesWithMessageType<DelayedResponse>()
            .Distinct()
            .Single().Envelope;

        envelope.Status.ShouldBe(EnvelopeStatus.Scheduled);
    }
}

public class TriggerHandler
{
    public SelfSender Handle(TriggerMessage message)
    {
        return new SelfSender(message.Id);
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

public record DelayedResponse(Guid Id) : DelayedMessage(5.Minutes());

public record TriggerMessage(Guid Id);

public record SelfSender(Guid Id) : ISendMyself
{
    public ValueTask ApplyAsync(IMessageContext context)
    {
        return context.SendAsync(new Cascaded(Id));
    }
}

public record Cascaded(Guid Id);
