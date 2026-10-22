using JasperFx.Core;
using NSubstitute;
using Shouldly;
using Wolverine;
using Wolverine.ComplianceTests;
using Wolverine.Persistence.Durability;
using Wolverine.Runtime;
using Wolverine.Runtime.WorkerQueues;
using Wolverine.Transports;
using Wolverine.Transports.Stub;
using Xunit;

namespace CoreTests.Runtime.WorkerQueues;

/// <summary>
/// GH-5119. A durable receiver settles the broker delivery BEFORE the handler runs (GH-4026), and a
/// transport may record that settle on <c>Envelope.HasBeenAcked</c> -- SQS does (GH-4489).
/// <c>MessageContext.CompleteAsync</c> short-circuited on that flag, so the durable receiver's own
/// completion -- marking the inbox row handled -- never ran, and the row sat 'Incoming', owned by the
/// live node, forever.
/// </summary>
public class durable_completion_ignores_the_broker_ack_flag_5119 : IAsyncLifetime
{
    private readonly Envelope theEnvelope = ObjectMother.Envelope();
    private readonly MockWolverineRuntime theRuntime = new();
    private DurableReceiver theReceiver = null!;

    public ValueTask InitializeAsync()
    {
        theReceiver = new DurableReceiver(new StubEndpoint("one", new StubTransport()), theRuntime,
            Substitute.For<IHandlerPipeline>());

        theEnvelope.Status = EnvelopeStatus.Incoming;
        theEnvelope.OwnerId = theRuntime.DurabilitySettings.AssignedNodeNumber;

        // What a transport like SQS leaves behind once the durable receiver's pre-handler settle deleted
        // the broker message
        theEnvelope.HasBeenAcked = true;

        return ValueTask.CompletedTask;
    }

    public ValueTask DisposeAsync()
    {
        theReceiver.SafeDispose();
        return ValueTask.CompletedTask;
    }

    [Fact]
    public async Task completing_through_a_durable_receiver_still_marks_the_inbox_row_handled()
    {
        var context = new MessageContext(theRuntime);
        context.ReadEnvelope(theEnvelope, theReceiver);

        await context.CompleteAsync();

        var deadline = DateTimeOffset.UtcNow.Add(5.Seconds());
        while (!inboxWasMarkedHandled() && DateTimeOffset.UtcNow < deadline)
        {
            await Task.Delay(50.Milliseconds(), TestContext.Current.CancellationToken);
        }

        inboxWasMarkedHandled().ShouldBeTrue();
    }

    [Fact]
    public async Task a_non_durable_channel_is_still_not_settled_twice()
    {
        // The control: for any other channel the flag means the broker delivery is already settled, and
        // completing again would double ack it
        var channel = Substitute.For<IChannelCallback>();

        var context = new MessageContext(theRuntime);
        context.ReadEnvelope(theEnvelope, channel);

        await context.CompleteAsync();

        await channel.DidNotReceive().CompleteAsync(theEnvelope);
    }

    private bool inboxWasMarkedHandled()
    {
        return theRuntime.Storage.Inbox.ReceivedCalls()
            .Any(x => x.GetMethodInfo().Name == nameof(IMessageInbox.MarkIncomingEnvelopeAsHandledAsync));
    }
}
