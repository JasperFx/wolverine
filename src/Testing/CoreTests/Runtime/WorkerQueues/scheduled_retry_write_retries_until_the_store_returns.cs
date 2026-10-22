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
/// F12 from the durable compliance rig, the GH-4664 class of bug in the one block GH-4664 missed. A failure handed to
/// <c>ScheduleRetry</c> writes the inbox row back as Scheduled for any node, through a RetryBlock with the default
/// budget of 3 retries over ~400ms. A database blip longer than that discarded the write, and the row stayed
/// Incoming and owned by this live node, which nothing reclaims until the node restarts.
/// </summary>
public class scheduled_retry_write_retries_until_the_store_returns : IAsyncLifetime
{
    private readonly Envelope theEnvelope = ObjectMother.Envelope();
    private readonly MockWolverineRuntime theRuntime = new();
    private DurableReceiver theReceiver = null!;

    private int _attempts;

    public ValueTask InitializeAsync()
    {
        theReceiver = new DurableReceiver(new StubEndpoint("f12", new StubTransport()), theRuntime,
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

    private void theStoreIsDownFor(int failures)
    {
        theRuntime.Storage.Inbox.ScheduleExecutionAsync(Arg.Any<Envelope>()).Returns(_ =>
        {
            Interlocked.Increment(ref _attempts);
            return _attempts <= failures
                ? Task.FromException(new TimeoutException("the message store is unreachable"))
                : Task.CompletedTask;
        });
    }

    [Fact]
    public async Task a_blip_longer_than_the_old_budget_still_schedules_the_retry()
    {
        // Six failures is past the 4 attempts the default block allows. Before the fix the 4th failure discarded
        // the reschedule and the row stayed Incoming, owned by this node.
        theStoreIsDownFor(6);

        await theReceiver.MoveToScheduledUntilAsync(theEnvelope, DateTimeOffset.UtcNow.AddSeconds(5));

        await waitForAttemptsAsync(7);

        _attempts.ShouldBe(7);
    }

    [Fact]
    public async Task the_reschedule_lands_on_the_first_try_when_the_store_is_healthy()
    {
        theStoreIsDownFor(0);

        await theReceiver.MoveToScheduledUntilAsync(theEnvelope, DateTimeOffset.UtcNow.AddSeconds(5));

        await waitForAttemptsAsync(1);

        _attempts.ShouldBe(1);
    }

    private async Task waitForAttemptsAsync(int expected)
    {
        var deadline = DateTimeOffset.UtcNow.Add(30.Seconds());
        while (_attempts < expected && DateTimeOffset.UtcNow < deadline)
        {
            await Task.Delay(100.Milliseconds());
        }
    }
}
