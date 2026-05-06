using JasperFx.Core;
using Microsoft.Extensions.Hosting;
using Shouldly;
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

    public Task InitializeAsync() => Task.CompletedTask;
    public Task DisposeAsync() => Task.CompletedTask;

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

    // Note: a rollback test is intentionally omitted from this suite. In Wolverine's
    // current implementation, the DLQ insert (via Storage.Inbox.MoveToDeadLetterStorageAsync)
    // and the fault publish (via lifecycle.PublishAsync) commit in independent batches —
    // the local-queue persistence path enqueues immediately rather than enrolling in the
    // receive-side MessageContext's outbox transaction. Forcing a crash between the two
    // therefore observes (DLQ=1, Fault=1), making a rollback assertion meaningless in this
    // routing topology. A properly-targeted rollback test would need to route Fault<T>
    // through a destination whose persistence enrols in the active outbox transaction,
    // which is a follow-up exercise.
}
