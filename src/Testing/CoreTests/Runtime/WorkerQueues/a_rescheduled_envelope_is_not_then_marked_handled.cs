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
/// F13 (durable compliance rig). A handler that calls <c>ReScheduleAsync</c> and then returns normally hands its inbox
/// row back as Scheduled -- and then the success path completes the envelope anyway. Since GH-5119 that completion
/// always reaches the durable receiver, whose mark-as-handled UPDATE has no status predicate, so the Scheduled row was
/// overwritten to Handled and the rescheduled run never happened. A rescheduled envelope is settled by its
/// reschedule, not by completing it.
/// </summary>
public class a_rescheduled_envelope_is_not_then_marked_handled : IAsyncLifetime
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
        theEnvelope.HasBeenAcked = true;

        return ValueTask.CompletedTask;
    }

    public ValueTask DisposeAsync()
    {
        theReceiver.SafeDispose();
        return ValueTask.CompletedTask;
    }

    [Fact]
    public async Task completing_after_the_handler_rescheduled_leaves_the_scheduled_row_alone()
    {
        var context = new MessageContext(theRuntime);
        context.ReadEnvelope(theEnvelope, theReceiver);

        // What the handler does, followed by what the success continuation does once it returns
        await context.ReScheduleAsync(DateTimeOffset.UtcNow.AddMinutes(1));
        await context.CompleteAsync();

        // The reschedule lands through the receiver's background block; give the mark-as-handled block every chance
        // to run after it
        await waitForAsync(() => inboxReceived(nameof(IMessageInbox.ScheduleExecutionAsync)));
        await Task.Delay(500.Milliseconds(), TestContext.Current.CancellationToken);

        inboxReceived(nameof(IMessageInbox.ScheduleExecutionAsync)).ShouldBeTrue();
        inboxReceived(nameof(IMessageInbox.MarkIncomingEnvelopeAsHandledAsync)).ShouldBeFalse();
    }

    [Fact]
    public async Task a_pooled_context_does_not_carry_the_reschedule_into_its_next_envelope()
    {
        var context = new MessageContext(theRuntime);
        context.ReadEnvelope(ObjectMother.Envelope(), theReceiver);
        await context.ReScheduleAsync(DateTimeOffset.UtcNow.AddMinutes(1));
        context.ClearState();

        // The next message through the same pooled context completes normally
        context.ReadEnvelope(theEnvelope, theReceiver);
        await context.CompleteAsync();

        await waitForAsync(() => inboxReceived(nameof(IMessageInbox.MarkIncomingEnvelopeAsHandledAsync)));
        inboxReceived(nameof(IMessageInbox.MarkIncomingEnvelopeAsHandledAsync)).ShouldBeTrue();
    }

    private static async Task waitForAsync(Func<bool> condition)
    {
        var deadline = DateTimeOffset.UtcNow.Add(5.Seconds());
        while (!condition() && DateTimeOffset.UtcNow < deadline)
        {
            await Task.Delay(50.Milliseconds(), TestContext.Current.CancellationToken);
        }
    }

    private bool inboxReceived(string methodName)
    {
        return theRuntime.Storage.Inbox.ReceivedCalls().Any(x => x.GetMethodInfo().Name == methodName);
    }
}
