using JasperFx.Core;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using JasperFx.Resources;
using NSubstitute;
using Shouldly;
using Wolverine.Persistence.Durability;
using Wolverine.RDBMS;
using Wolverine.RDBMS.Durability;
using Wolverine.RDBMS.Polling;
using Wolverine.Runtime;
using Wolverine.Tracking;
using Wolverine.Transports;
using Xunit;

namespace Wolverine.ComplianceTests;

/// <summary>
/// GH-5022. <see cref="DurabilitySettings.InboxStaleTime" /> releases an owned inbox row once its <c>timestamp</c>
/// is older than the setting. That column was only ever its insert default, so it measured how long ago the row
/// was first written, not how long it has been stuck: a durable retry or a recovered row that a node had only
/// just claimed was released again at once, and handed back with the same old timestamp every recovery cycle.
/// Claiming a row -- recovery reassignment, or promoting a due scheduled envelope -- now restarts its clock, and
/// the sweep only releases rows that are waiting to run (status Incoming).
/// </summary>
/// <remarks>
/// The host a subclass builds must set <c>InboxStaleTime</c> (to anything over a few minutes); the column only
/// exists then.
/// </remarks>
public abstract class InboxStaleTimeCompliance : IAsyncLifetime
{
    private const int ClaimingNode = 5;

    protected IHost theHost = null!;
    protected IMessageStore theStore = null!;

    public abstract Task<IHost> BuildCleanHost();

    /// <summary>
    /// Move the envelope's inbox <c>timestamp</c> back by <paramref name="age" />, as if it had been written then
    /// </summary>
    public abstract Task BackdateAsync(Envelope envelope, TimeSpan age);

    public async ValueTask InitializeAsync()
    {
        theHost = await BuildCleanHost();
        await theHost.ResetResourceState();

        theStore = theHost.GetRuntime().Storage;
        theHost.GetRuntime().Options.Durability.InboxStaleTime.ShouldNotBeNull();
    }

    public async ValueTask DisposeAsync()
    {
        await theHost.StopAsync();
        theHost.Dispose();
    }

    private TimeSpan longerThanTheStaleTime =>
        theHost.GetRuntime().Options.Durability.InboxStaleTime!.Value.Add(1.Hours());

    private async Task runTheStaleSweepAsync()
    {
        var database = (IMessageDatabase)theStore;
        var operation = new BumpStaleIncomingEnvelopesOperation(
            database.DbObjectNameFor(DatabaseConstants.IncomingTable),
            theHost.GetRuntime().Options.Durability, DateTimeOffset.UtcNow);

        await theHost.InvokeAsync(new DatabaseOperationBatch(database, [operation]));
    }

    private async Task<Envelope> theInboxRowFor(Envelope envelope)
    {
        var incoming = await theStore.Admin.AllIncomingAsync();
        return incoming.Single(x => x.Id == envelope.Id);
    }

    [Fact]
    public async Task an_owned_incoming_row_older_than_the_stale_time_is_released()
    {
        var envelope = ObjectMother.Envelope();
        envelope.Status = EnvelopeStatus.Incoming;
        envelope.OwnerId = ClaimingNode;
        await theStore.Inbox.StoreIncomingAsync(envelope);

        await BackdateAsync(envelope, longerThanTheStaleTime);
        await runTheStaleSweepAsync();

        (await theInboxRowFor(envelope)).OwnerId.ShouldBe(0);
    }

    [Fact]
    public async Task an_owned_incoming_row_inside_the_stale_time_is_left_alone()
    {
        var envelope = ObjectMother.Envelope();
        envelope.Status = EnvelopeStatus.Incoming;
        envelope.OwnerId = ClaimingNode;
        await theStore.Inbox.StoreIncomingAsync(envelope);

        await runTheStaleSweepAsync();

        (await theInboxRowFor(envelope)).OwnerId.ShouldBe(ClaimingNode);
    }

    [Fact]
    public async Task recovery_reassignment_restarts_the_stale_clock()
    {
        // A row released to recovery long ago, then claimed by a node
        var envelope = ObjectMother.Envelope();
        envelope.Status = EnvelopeStatus.Incoming;
        envelope.OwnerId = TransportConstants.AnyNode;
        await theStore.Inbox.StoreIncomingAsync(envelope);
        await BackdateAsync(envelope, longerThanTheStaleTime);

        await theStore.ReassignIncomingAsync(ClaimingNode, [envelope]);
        await runTheStaleSweepAsync();

        // Before GH-5022 it kept its old timestamp, so the very next sweep released it again
        (await theInboxRowFor(envelope)).OwnerId.ShouldBe(ClaimingNode);
    }

    [Fact]
    public async Task promoting_a_due_scheduled_envelope_restarts_the_stale_clock()
    {
        // A durable retry: the row was written long ago, and its retry is due now
        var envelope = ObjectMother.Envelope();
        envelope.Status = EnvelopeStatus.Scheduled;
        envelope.OwnerId = TransportConstants.AnyNode;
        envelope.ScheduledTime = DateTimeOffset.UtcNow.Subtract(1.Minutes());
        await theStore.Inbox.StoreIncomingAsync(envelope);
        await BackdateAsync(envelope, longerThanTheStaleTime);

        // A stand-in runtime, so the promoted envelope is not actually run
        var settings = theHost.GetRuntime().DurabilitySettings;
        await ((IMessageDatabase)theStore).PollForScheduledMessagesAsync(Substitute.For<IWolverineRuntime>(),
            NullLogger.Instance, settings, CancellationToken.None);

        var promoted = await theInboxRowFor(envelope);
        promoted.Status.ShouldBe(EnvelopeStatus.Incoming);
        promoted.OwnerId.ShouldBe(settings.AssignedNodeNumber);

        await runTheStaleSweepAsync();

        // Before GH-5022 the retry was released the moment it fired
        (await theInboxRowFor(envelope)).OwnerId.ShouldBe(settings.AssignedNodeNumber);
    }

    [Fact]
    public async Task the_stale_sweep_leaves_scheduled_rows_alone()
    {
        var envelope = ObjectMother.Envelope();
        envelope.Status = EnvelopeStatus.Scheduled;
        envelope.OwnerId = ClaimingNode;
        envelope.ScheduledTime = DateTimeOffset.UtcNow.Add(1.Days());
        await theStore.Inbox.StoreIncomingAsync(envelope);
        await BackdateAsync(envelope, longerThanTheStaleTime);

        await runTheStaleSweepAsync();

        // Not waiting to run, so not stuck: the scheduled-job path owns it until it is due
        (await theInboxRowFor(envelope)).OwnerId.ShouldBe(ClaimingNode);
    }

    private IMessageInboxHeartbeat theHeartbeat =>
        theStore.Inbox as IMessageInboxHeartbeat
        ?? throw new InvalidOperationException($"{theStore.GetType().Name} does not support the InboxStaleTime heartbeat");

    [Fact]
    public async Task the_heartbeat_restarts_the_clock_of_a_row_this_node_holds()
    {
        // A handler that has been running longer than the stale time
        var envelope = ObjectMother.Envelope();
        envelope.Status = EnvelopeStatus.Incoming;
        envelope.OwnerId = ClaimingNode;
        await theStore.Inbox.StoreIncomingAsync(envelope);
        await BackdateAsync(envelope, longerThanTheStaleTime);

        await theHeartbeat.RestartStaleClockAsync(ClaimingNode, [envelope]);
        await runTheStaleSweepAsync();

        (await theInboxRowFor(envelope)).OwnerId.ShouldBe(ClaimingNode);
    }

    [Fact]
    public async Task the_heartbeat_does_not_keep_alive_a_row_another_node_owns()
    {
        var envelope = ObjectMother.Envelope();
        envelope.Status = EnvelopeStatus.Incoming;
        envelope.OwnerId = ClaimingNode + 1;
        await theStore.Inbox.StoreIncomingAsync(envelope);
        await BackdateAsync(envelope, longerThanTheStaleTime);

        // This node does not hold it, so its heartbeat must not keep it alive
        await theHeartbeat.RestartStaleClockAsync(ClaimingNode, [envelope]);
        await runTheStaleSweepAsync();

        (await theInboxRowFor(envelope)).OwnerId.ShouldBe(0);
    }
}
