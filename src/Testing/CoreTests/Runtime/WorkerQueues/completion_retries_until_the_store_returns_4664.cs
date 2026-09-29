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
/// GH-4664. A handler of a durable queue finishes while the message store is briefly unreachable. Marking
/// the envelope handled failed, the receiver's <c>RetryBlock</c> gave up after 3 retries over ~400 ms and
/// discarded the completion with one line at Information, and the envelope's inbox row then sat
/// <c>Incoming</c> and owned by <b>this, live</b> node.
///
/// <para>
/// That is the one state nothing reclaims. <c>CheckRecoverableIncomingMessagesOperation</c> reads
/// <c>owner_id = 0</c> only, and the dead-node sweep deliberately never releases a live node's rows
/// (GH-3850). So the message waited for this node's next restart — days, in a long-running service — and
/// <c>DurabilitySettings.InboxStaleTime</c>, whose own documentation says it "should NOT ever be
/// necessary", was the only way back. It is also an imperfect one: a scheduled retry never refreshes the
/// <c>timestamp</c> column, and on PostgreSQL that column's default was skewed by the session time zone
/// (GH-4663).
/// </para>
///
/// <para>
/// Unlike the first write of an envelope (GH-4662), there is nothing to hand back to a caller here and
/// nothing to lose by waiting: the row is already durable, this node still owns it so nothing else can
/// pick it up twice, and the handler has already run. So the completion now retries until the store comes
/// back or the host shuts down.
/// </para>
/// </summary>
public class completion_retries_until_the_store_returns_4664 : IAsyncLifetime
{
    private readonly Envelope theEnvelope = ObjectMother.Envelope();
    private readonly MockWolverineRuntime theRuntime = new();
    private DurableReceiver theReceiver = null!;

    private int _attempts;

    public ValueTask InitializeAsync()
    {
        theReceiver = new DurableReceiver(new StubEndpoint("one", new StubTransport()), theRuntime,
            Substitute.For<IHandlerPipeline>());

        theEnvelope.Status = EnvelopeStatus.Incoming;
        theEnvelope.OwnerId = theRuntime.DurabilitySettings.AssignedNodeNumber;

        return ValueTask.CompletedTask;
    }

    public ValueTask DisposeAsync()
    {
        theReceiver.SafeDispose();
        return ValueTask.CompletedTask;
    }

    /// <summary>The store refuses the first <paramref name="failures" /> completions, then accepts.</summary>
    private void theStoreIsDownFor(int failures)
    {
        theRuntime.Storage.Inbox.MarkIncomingEnvelopeAsHandledAsync(Arg.Any<Envelope>()).Returns(_ =>
        {
            _attempts++;
            return _attempts <= failures
                ? Task.FromException(new TimeoutException("the message store is unreachable"))
                : Task.CompletedTask;
        });
    }

    [Fact]
    public async Task an_outage_longer_than_the_old_budget_still_completes()
    {
        // Six failures is well past the 4 attempts the old block allowed, and past its ~400ms window.
        // Before the fix the completion was discarded on the 4th and the row stayed owned by this node.
        theStoreIsDownFor(6);

        await theReceiver.CompleteAsync(theEnvelope);

        await waitForAttemptsAsync(7);

        _attempts.ShouldBe(7);
    }

    [Fact]
    public async Task the_completion_still_lands_on_the_first_try_when_the_store_is_healthy()
    {
        // The control: no outage, no extra attempts, no added latency on the happy path.
        theStoreIsDownFor(0);

        await theReceiver.CompleteAsync(theEnvelope);

        await waitForAttemptsAsync(1);

        _attempts.ShouldBe(1);
    }

    private async Task waitForAttemptsAsync(int expected)
    {
        // The ramp reaches 1s on the 4th retry, so allow for the real pauses rather than polling tightly.
        var deadline = DateTimeOffset.UtcNow.Add(30.Seconds());
        while (_attempts < expected && DateTimeOffset.UtcNow < deadline)
        {
            await Task.Delay(100.Milliseconds());
        }
    }
}
