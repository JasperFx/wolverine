using Shouldly;
using Wolverine;
using Wolverine.Persistence.Durability;
using Wolverine.Runtime.Agents;
using Xunit;

namespace CoreTests.Persistence;

// A storeless host (Solo / NullMessageStore) is observed by diagnostics tooling such as CritterWatch,
// which calls into the store (e.g. Nodes to persist NodeRecords). The null store must therefore never
// throw — every member no-ops or returns non-null/empty. Previously several members threw
// NotSupportedException / NotImplementedException, crashing the observer (the reported
// "Error trying to persist node records" → NullMessageStore.get_Nodes()).
public class null_message_store_never_throws
{
    private readonly NullMessageStore theStore = new();

    [Fact]
    public void nodes_is_a_usable_no_op_not_a_throw()
    {
        // The exact CritterWatch crash path: get Nodes, then log a node record.
        var nodes = theStore.Nodes;
        nodes.ShouldNotBeNull();

        Should.NotThrow(async () =>
        {
            await nodes.LogRecordsAsync(new NodeRecord
            {
                NodeNumber = 1,
                RecordType = NodeRecordType.NodeStarted,
                ServiceName = "test"
            });

            (await nodes.LoadAllNodesAsync(CancellationToken.None)).ShouldBeEmpty();
            (await nodes.FetchRecentRecordsAsync(10)).ShouldBeEmpty();
            nodes.HasLeadershipLock().ShouldBeFalse();
            (await nodes.TryAttainLeadershipLockAsync(CancellationToken.None)).ShouldBeFalse();
        });
    }

    [Fact]
    public void formerly_throwing_members_now_no_op_or_return_empty()
    {
        Should.NotThrow(async () =>
        {
            (await theStore.LoadOutgoingAsync(new Uri("local://one"))).ShouldBeEmpty();
            (await theStore.LoadScheduledToExecuteAsync(DateTimeOffset.UtcNow)).ShouldBeEmpty();
            (await theStore.LoadPageOfGloballyOwnedIncomingAsync(new Uri("local://one"), 10)).ShouldBeEmpty();

            await theStore.ReassignOutgoingAsync(1, Array.Empty<Envelope>());
            await theStore.ReassignIncomingAsync(1, Array.Empty<Envelope>());

            (await theStore.DeadLetterEnvelopeByIdAsync(Guid.NewGuid())).ShouldBeNull();
        });
    }

    [Fact]
    public void start_scheduled_jobs_returns_a_non_null_agent()
    {
        // The no-op store has no durable scheduled-job agent, but must hand back a usable agent.
        var agent = theStore.StartScheduledJobs(null!);
        agent.ShouldNotBeNull();
        agent.Uri.ShouldNotBeNull();
    }

    [Fact]
    public void storing_a_scheduled_envelope_without_a_time_does_nothing_rather_than_throwing()
    {
        var scheduled = new Envelope { Status = EnvelopeStatus.Scheduled, ScheduledTime = null };

        Should.NotThrow(async () =>
        {
            await theStore.StoreIncomingAsync(scheduled);
            await theStore.RescheduleExistingEnvelopeForRetryAsync(scheduled);
        });
    }
}
