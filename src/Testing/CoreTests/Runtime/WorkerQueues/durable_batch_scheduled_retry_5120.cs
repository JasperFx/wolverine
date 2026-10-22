using JasperFx.Core;
using NSubstitute;
using Shouldly;
using Wolverine;
using Wolverine.ComplianceTests;
using Wolverine.Runtime;
using Wolverine.Runtime.WorkerQueues;
using Wolverine.Transports.Stub;
using Xunit;

namespace CoreTests.Runtime.WorkerQueues;

/// <summary>
/// GH-5120. A native batch on a durable queue is a carrier envelope with no inbox row of its own -- only its
/// members are persisted. Rescheduling it for a retry UPDATE-d the inbox row for the carrier's id, matched
/// nothing, and the retry was silently lost. The carrier's retry is now held in memory and the SAME carrier
/// comes back, so its Attempts keeps counting toward the error policy's limit.
/// </summary>
public class durable_batch_scheduled_retry_5120 : IAsyncLifetime
{
    private readonly MockWolverineRuntime theRuntime = new();
    private readonly IHandlerPipeline thePipeline = Substitute.For<IHandlerPipeline>();
    private DurableReceiver theReceiver = null!;

    public ValueTask InitializeAsync()
    {
        theReceiver = new DurableReceiver(new StubEndpoint("one", new StubTransport()), theRuntime, thePipeline);
        return ValueTask.CompletedTask;
    }

    public ValueTask DisposeAsync()
    {
        theReceiver.SafeDispose();
        return ValueTask.CompletedTask;
    }

    private static Envelope aBatchCarrier()
    {
        var members = new[] { ObjectMother.Envelope(), ObjectMother.Envelope() };
        return new Envelope(new[] { "one", "two" }, members) { Attempts = 1 };
    }

    [Fact]
    public async Task a_batch_carrier_is_re_executed_after_its_scheduled_time()
    {
        var carrier = aBatchCarrier();

        await theReceiver.MoveToScheduledUntilAsync(carrier, DateTimeOffset.UtcNow.AddMilliseconds(100));

        var deadline = DateTimeOffset.UtcNow.Add(5.Seconds());
        while (!theCarrierWasExecuted(carrier) && DateTimeOffset.UtcNow < deadline)
        {
            await Task.Delay(50.Milliseconds(), TestContext.Current.CancellationToken);
        }

        theCarrierWasExecuted(carrier).ShouldBeTrue();

        // The same carrier came back, so the error policy sees its attempts so far
        carrier.Attempts.ShouldBe(1);
    }

    [Fact]
    public async Task a_batch_carrier_is_never_rescheduled_through_the_inbox()
    {
        // The carrier has no row, so an inbox reschedule could only ever be a silent no-op
        await theReceiver.MoveToScheduledUntilAsync(aBatchCarrier(), DateTimeOffset.UtcNow.AddMinutes(5));

        await Task.Delay(250.Milliseconds(), TestContext.Current.CancellationToken);

        await theRuntime.Storage.Inbox.DidNotReceive().ScheduleExecutionAsync(Arg.Any<Envelope>());
    }

    [Fact]
    public async Task a_pending_batch_retry_does_not_fire_after_the_receiver_drains()
    {
        // The drain hands the members' rows back to any node; a retry firing after that would run them twice
        var carrier = aBatchCarrier();
        await theReceiver.MoveToScheduledUntilAsync(carrier, DateTimeOffset.UtcNow.AddMilliseconds(300));

        await theReceiver.DrainAsync();

        await Task.Delay(600.Milliseconds(), TestContext.Current.CancellationToken);

        theCarrierWasExecuted(carrier).ShouldBeFalse();
    }

    private bool theCarrierWasExecuted(Envelope carrier)
    {
        return thePipeline.ReceivedCalls().Any(x => x.GetArguments().Contains(carrier));
    }
}
