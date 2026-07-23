using JasperFx;
using JasperFx.Core;
using Microsoft.Extensions.Hosting;
using Shouldly;
using Wolverine.Configuration;
using Wolverine.Persistence.Durability;
using Wolverine.Runtime;
using Wolverine.Tracking;
using Wolverine.Transports;
using Xunit;

namespace Wolverine.ComplianceTests;

/// <summary>
/// Compliance suite for <see cref="StorageExtensions.ClearAllWolverineStorageAsync"/> against a
/// database-backed queue transport. A provider opts in with nothing but its own storage and
/// transport bootstrapping: register a durable queue named <see cref="QueueName"/> that nothing
/// is listening to, and every scenario below comes along for free.
///
/// This replaces the per-provider "reset also truncates the queue tables" behavior that briefly
/// lived on <c>ClearAllAsync()</c> / <c>RebuildAsync()</c> — see GH-3592. Those keep their
/// long-standing envelope-storage-only semantics; the full reset is opt-in through this extension.
/// </summary>
public abstract class ClearAllWolverineStorageCompliance : IAsyncLifetime
{
    /// <summary>
    /// Name of the database-backed queue every implementation must register. Register it as a
    /// <i>subscriber</i> only — never attach a listener. Throttling a listener's polling interval
    /// is not enough: several of these transports poll once on startup regardless, and the row
    /// disappears out from under the assertions before the reset ever runs.
    /// </summary>
    public const string QueueName = "resetone";

    protected IHost theHost = null!;
    protected IBrokerQueue theQueue = null!;
    protected IMessageStore theMessageStore = null!;

    /// <summary>
    /// Register durable message storage plus the database-backed queue transport, including a
    /// queue named <see cref="QueueName"/>. Do not attach a listener to that queue.
    /// </summary>
    protected abstract void ConfigureStorage(WolverineOptions options);

    /// <summary>
    /// Put the envelope directly on the queue, bypassing routing and the outbox so the row
    /// lands in the queue table deterministically. Every database queue exposes
    /// <c>SendAsync(Envelope)</c>, but not through a shared interface, hence the hook.
    /// </summary>
    protected abstract ValueTask sendToQueueAsync(Envelope envelope);

    /// <summary>
    /// Optional per-provider cleanup that has to happen before the host is built — dropping a
    /// schema, recreating a database, and so on.
    /// </summary>
    protected virtual Task beforeHostAsync()
    {
        return Task.CompletedTask;
    }

    public async Task InitializeAsync()
    {
        await beforeHostAsync();

        theHost = await Host.CreateDefaultBuilder()
            .UseWolverine(opts =>
            {
                ConfigureStorage(opts);

                // Keep the durability agents out of it; this suite is about the reset call itself.
                opts.Durability.Mode = DurabilityMode.Solo;
            }).StartAsync();

        var runtime = theHost.GetRuntime();

        theQueue = runtime.Options.Transports
            .SelectMany(x => x.Endpoints())
            .OfType<IBrokerQueue>()
            .Single(x => x is IDatabaseBackedEndpoint && ((Endpoint)x).EndpointName == QueueName);

        theMessageStore = runtime.Storage;

        // Start from empty envelope storage. Several providers share one schema across their whole
        // test suite, so without this the seed assertions below ("exactly one incoming envelope")
        // are at the mercy of whatever ran first. ClearAllAsync() is the long-standing
        // envelope-storage-only reset -- deliberately not the method under test.
        await theMessageStore.Admin.ClearAllAsync();
    }

    public virtual async Task DisposeAsync()
    {
        await theHost.StopAsync();
        theHost.Dispose();
    }

    protected async Task<(long Queued, long Scheduled)> queueCountsAsync()
    {
        var attributes = await theQueue.GetAttributesAsync();
        return (long.Parse(attributes["Count"]), long.Parse(attributes["Scheduled"]));
    }

    /// <summary>
    /// One row in the queue table, one in its scheduled-message table.
    /// </summary>
    protected async Task seedQueueAsync()
    {
        var immediate = ObjectMother.Envelope();
        immediate.DeliverBy = DateTimeOffset.UtcNow.AddHours(1);
        await sendToQueueAsync(immediate);

        var scheduled = ObjectMother.Envelope();
        scheduled.ScheduleDelay = 1.Hours();
        scheduled.DeliverBy = DateTimeOffset.UtcNow.AddHours(1);
        await sendToQueueAsync(scheduled);

        var counts = await queueCountsAsync();
        counts.Queued.ShouldBe(1);
        counts.Scheduled.ShouldBe(1);
    }

    protected async Task seedEnvelopeStorageAsync()
    {
        var incoming = ObjectMother.Envelope();
        incoming.Status = EnvelopeStatus.Incoming;
        incoming.OwnerId = TransportConstants.AnyNode;
        await theMessageStore.Inbox.StoreIncomingAsync(incoming);

        var outgoing = ObjectMother.Envelope();
        outgoing.Status = EnvelopeStatus.Outgoing;
        outgoing.OwnerId = TransportConstants.AnyNode;
        await theMessageStore.Outbox.StoreOutgoingAsync(outgoing, TransportConstants.AnyNode);

        var counts = await theMessageStore.Admin.FetchCountsAsync();
        counts.Incoming.ShouldBe(1);
        counts.Outgoing.ShouldBe(1);
    }

    [Fact]
    public async Task empties_the_queue_and_scheduled_message_tables()
    {
        await seedQueueAsync();

        await theHost.ClearAllWolverineStorageAsync();

        var counts = await queueCountsAsync();
        counts.Queued.ShouldBe(0);
        counts.Scheduled.ShouldBe(0);
    }

    [Fact]
    public async Task empties_envelope_storage_too()
    {
        await seedEnvelopeStorageAsync();

        await theHost.ClearAllWolverineStorageAsync();

        var counts = await theMessageStore.Admin.FetchCountsAsync();
        counts.Incoming.ShouldBe(0);
        counts.Outgoing.ShouldBe(0);
    }

    [Fact]
    public async Task clear_all_async_alone_leaves_the_queue_tables_alone()
    {
        // The negative control for GH-3592: ClearAllAsync() is envelope storage only. If this
        // starts failing, the reverted reset semantics have crept back in.
        //
        // Deliberately ClearAllAsync() rather than RebuildAsync(): RebuildAsync() also runs a
        // schema migration, and on providers that drop and recreate every table registered
        // through AddTable (Oracle) that migration empties the queue tables as a pre-existing
        // side effect. That is provider-specific and unrelated to reset semantics.
        await seedQueueAsync();

        await theMessageStore.Admin.ClearAllAsync();

        var counts = await queueCountsAsync();
        counts.Queued.ShouldBe(1);
        counts.Scheduled.ShouldBe(1);
    }

    [Fact]
    public async Task rebuilds_queue_tables_that_have_been_dropped()
    {
        // "Built but empty" — the built half. Tearing the endpoint down drops both the queue
        // table and its scheduled-message table.
        await theQueue.TeardownAsync(theHost.GetRuntime().Logger);

        // Precondition: the tables really are gone, so writing to the queue blows up. Probing by
        // writing rather than through CheckAsync() or a count: Weasel's schema diff throws an NRE
        // against a table that is entirely absent rather than reporting a difference, and some
        // providers' CountAsync swallows the missing-table error and reports zero.
        var missing = false;
        try
        {
            await sendToQueueAsync(ObjectMother.Envelope());
        }
        catch (Exception)
        {
            missing = true;
        }

        missing.ShouldBeTrue();

        await theHost.ClearAllWolverineStorageAsync();

        // Rebuilt: seeding asserts one row in each table, which is only reachable if both are back.
        await seedQueueAsync();

        // ...and still empty-able.
        await theHost.ClearAllWolverineStorageAsync();

        var counts = await queueCountsAsync();
        counts.Queued.ShouldBe(0);
        counts.Scheduled.ShouldBe(0);
    }

    [Fact]
    public async Task is_idempotent()
    {
        await seedQueueAsync();
        await seedEnvelopeStorageAsync();

        await theHost.ClearAllWolverineStorageAsync();
        await theHost.ClearAllWolverineStorageAsync();

        var queueCounts = await queueCountsAsync();
        queueCounts.Queued.ShouldBe(0);
        queueCounts.Scheduled.ShouldBe(0);

        var storeCounts = await theMessageStore.Admin.FetchCountsAsync();
        storeCounts.Incoming.ShouldBe(0);
        storeCounts.Outgoing.ShouldBe(0);
    }
}
