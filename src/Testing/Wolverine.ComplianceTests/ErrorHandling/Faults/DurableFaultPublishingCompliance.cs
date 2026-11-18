using JasperFx.Core;
using Microsoft.Extensions.Hosting;
using Shouldly;
using Wolverine.Runtime;
using Wolverine.Tracking;
using Xunit;

namespace Wolverine.ComplianceTests.ErrorHandling.Faults;

public record DurableSnapshot(int DeadLetterRowCount, int OutgoingFaultRowCount);

public abstract class DurableFaultPublishingCompliance : IAsyncLifetime
{
    /// <summary>
    /// Configure the receiver-side host. Setup MUST:
    ///  - Register durable persistence with a dedicated schema/database for this suite.
    ///  - Register AlwaysFailsHandler so OrderPlaced terminally fails after one attempt.
    ///  - Set OnException&lt;Exception&gt;().MoveToErrorQueue() (no retries).
    ///  - Call PublishFaultEvents() globally.
    ///  - Lock <c>Durability.KeepAfterMessageHandling</c> to a value &gt;= 5 minutes; the
    ///    snapshot assertion depends on processed envelopes still being present.
    ///  - Apply optionalCompose before StartAsync so individual tests can inject decorators.
    /// </summary>
    public abstract Task<IHost> BuildCleanHostAsync(Action<WolverineOptions>? optionalCompose = null);

    /// <summary>
    /// Snapshot the persisted DLQ row count and the outgoing-fault-envelope row count
    /// from the receiver's durable store. Used to verify the no-leak semantics:
    ///   happy path → both rows present (1, 1)
    ///   rollback   → DLQ row stays committed, outgoing fault rolled back (1, 0)
    /// </summary>
    protected abstract Task<DurableSnapshot> SnapshotAsync(IHost host);

    public ValueTask InitializeAsync() => ValueTask.CompletedTask;
    public ValueTask DisposeAsync() => ValueTask.CompletedTask;

    [Fact]
    public async Task smoke_durable_terminal_failure_publishes_fault()
    {
        var host = await BuildCleanHostAsync();
        try
        {
            var session = await host.TrackActivity()
                .DoNotAssertOnExceptionsDetected()
                .Timeout(30.Seconds())
                .PublishMessageAndWaitAsync(new OrderPlaced("smoke-1"));

            var fault = session.AutoFaultsPublished
                .MessagesOf<Fault<OrderPlaced>>()
                .Single();
            fault.Message.OrderId.ShouldBe("smoke-1");

            var record = session.AutoFaultsPublished
                .Envelopes()
                .Single(e => e.Message is Fault<OrderPlaced>);
            record.Headers[FaultHeaders.AutoPublished].ShouldBe("true");
        }
        finally
        {
            await host.StopAsync();
            host.Dispose();
        }
    }

    [Fact]
    public async Task happy_path_persists_both_dlq_and_fault_rows()
    {
        var host = await BuildCleanHostAsync();
        try
        {
            await host.TrackActivity()
                .DoNotAssertOnExceptionsDetected()
                .Timeout(30.Seconds())
                .PublishMessageAndWaitAsync(new OrderPlaced("atom-1"));

            var snapshot = await SnapshotAsync(host);
            snapshot.DeadLetterRowCount.ShouldBe(1);
            snapshot.OutgoingFaultRowCount.ShouldBe(1);
        }
        finally
        {
            await host.StopAsync();
            host.Dispose();
        }
    }

    // GH-5554: the fault is written in the same transaction as the dead-letter move. A crash right after that move
    // commits leaves the fault written, waiting to be recovered, where it used to be lost
    [Fact]
    public async Task a_crash_right_after_the_dead_letter_move_leaves_the_fault_written()
    {
        var host = await BuildCleanHostAsync();
        try
        {
            host.GetRuntime().DeadLetterHooks.CrashAfterNextMove();

            var session = await host.TrackActivity()
                .DoNotAssertOnExceptionsDetected()
                .Timeout(30.Seconds())
                .PublishMessageAndWaitAsync(new OrderPlaced("crash-1"));

            // Nothing was sent after the "crash"
            session.AutoFaultsPublished.MessagesOf<Fault<OrderPlaced>>().ShouldBeEmpty();
            host.GetRuntime().DeadLetterHooks.Stranded.Count.ShouldBe(1);

            var snapshot = await SnapshotAsync(host);
            snapshot.DeadLetterRowCount.ShouldBe(1);
            snapshot.OutgoingFaultRowCount.ShouldBe(1);
        }
        finally
        {
            await host.StopAsync();
            host.Dispose();
        }
    }

    // GH-5554: a dead-letter move that rolls back takes its fault with it, and the message stays in the inbox
    [Fact]
    public async Task a_dead_letter_move_that_rolls_back_takes_the_fault_with_it()
    {
        var host = await BuildCleanHostAsync();
        try
        {
            host.GetRuntime().DeadLetterHooks.FailNextMove();

            await host.TrackActivity()
                .DoNotAssertOnExceptionsDetected()
                .Timeout(30.Seconds())
                .WaitForMessageToBeRequeued<OrderPlaced>()
                .PublishMessageAndWaitAsync(new OrderPlaced("rollback-1"));

            var snapshot = await SnapshotAsync(host);
            snapshot.DeadLetterRowCount.ShouldBe(0);
            snapshot.OutgoingFaultRowCount.ShouldBe(0);
        }
        finally
        {
            host.GetRuntime().DeadLetterHooks.Reset();
            await host.StopAsync();
            host.Dispose();
        }
    }
}
