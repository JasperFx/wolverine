using IntegrationTests;
using JasperFx.Resources;
using Marten;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using Npgsql;
using NSubstitute;
using Shouldly;
using Wolverine;
using Wolverine.ComplianceTests;
using Wolverine.Marten;
using Wolverine.Persistence.Durability;
using Wolverine.RDBMS;
using Wolverine.Runtime;
using Wolverine.Transports.Tcp;
using Wolverine.Util;

namespace PostgresqlTests.Bugs;

// GH-4202: with EnableInboxPartitioning the incoming table is PARTITION BY LIST (status) and status is
// part of the primary key, so one identity can legally hold both a Scheduled row (a retry) and an Incoming
// row (a redelivery). Promoting the scheduled row by id alone moved it across partitions onto an identity
// the incoming partition already held, and the resulting 23505 rolled back the whole polling transaction --
// wedging every other due scheduled message, forever.
//
// The two _is_accepted_by_the_partitioned_key tests characterize duplicate states this fix does not close.
// They pin one mechanism only: Inbox.StoreIncomingAsync is a plain insert whose sole dedup is the
// per-partition unique constraint. Eager idempotency (MessageContext.AssertEagerIdempotencyAsync ->
// Inbox.ExistsAsync) is a separate path with no status predicate, and it is not exercised here.
//
// The patched promotion statement carries two predicates and only the identity match is pinned on its own:
// a_scheduled_sibling_at_another_destination_is_left_alone fails when the match is widened to the id alone.
// The status predicate is defensive. Nothing here can distinguish it from a no-op, because the discard step
// runs first and leaves no promotable row whose identity still holds an Incoming or a Handled sibling; it
// guards the window between those two statements, which these tests cannot force open.
//
// The raw SQL in the_unqualified_promotion_statement_raises_a_unique_violation is a deliberate verbatim
// copy of the pre-fix statement. It must stay unqualified: rewriting it to match the patched statement
// would silently remove the evidence that the constraint fires at all.
public abstract class PartitionedInboxScheduledPromotionContext : IAsyncLifetime
{
    private const int RetryAttempts = 3;
    private const int RedeliveryAttempts = 2;

    private readonly int thePort = PortFinder.GetAvailablePort();

    private IHost? theHost;

    protected IMessageStore thePersistence = null!;

    protected abstract MessageIdentity identityStyle { get; }
    protected abstract string schemaName { get; }

    protected abstract Uri redeliveryDestinationFor(Envelope original);

    public async ValueTask InitializeAsync()
    {
        theHost = await Host.CreateDefaultBuilder()
            .UseWolverine(opts =>
            {
                opts.Durability.EnableInboxPartitioning = true;
                opts.Durability.MessageIdentity = identityStyle;

                // MessageDatabase.StartScheduledJobs pumps this same poller on any non-tenant store whatever
                // DurabilityAgentEnabled says, and every row built here is already due, so the timer has to be
                // pushed past the test rather than switched off.
                opts.Durability.ScheduledJobFirstExecution = TimeSpan.FromHours(1);
                opts.Durability.ScheduledJobPollingTime = TimeSpan.FromHours(1);

                opts.Services.AddMarten(x =>
                {
                    x.Connection(Servers.PostgresConnectionString);
                    x.DatabaseSchemaName = schemaName;
                }).IntegrateWithWolverine();

                opts.ListenAtPort(thePort).UseDurableInbox();

                opts.Services.AddResourceSetupOnStartup();
            }).StartAsync();

        thePersistence = theHost.Services.GetRequiredService<IMessageStore>();

        await theHost.ResetResourceState();
    }

    public async ValueTask DisposeAsync()
    {
        if (theHost is null) return;

        await theHost.StopAsync();
        theHost.Dispose();
    }

    protected static Envelope dueScheduledEnvelope()
    {
        var envelope = ObjectMother.Envelope();
        envelope.Status = EnvelopeStatus.Incoming;
        envelope.ScheduledTime = DateTimeOffset.UtcNow.AddMinutes(-1);
        return envelope;
    }

    [Fact]
    public async Task redelivery_while_scheduled_for_retry_is_accepted_by_the_partitioned_key()
    {
        var envelope = await createDuplicatedIdentityAsync();

        var counts = await thePersistence.Admin.FetchCountsAsync();

        counts.Incoming.ShouldBe(1, "The partitioned key does not reject the redelivery: it lands in a different partition than the scheduled retry.");
        counts.Scheduled.ShouldBe(1, "The store leaves both rows in place. Reconciliation happens in the poller -- see promotion_converges_on_a_single_row.");

        (await statusesForAsync(envelope.Id)).ShouldBe(["Incoming", "Scheduled"]);
    }

    [Fact]
    public async Task redelivery_alongside_a_handled_row_is_accepted_by_the_partitioned_key()
    {
        var envelope = ObjectMother.Envelope();
        envelope.Status = EnvelopeStatus.Incoming;

        await thePersistence.Inbox.StoreIncomingAsync(envelope);
        await thePersistence.Inbox.MarkIncomingEnvelopeAsHandledAsync(envelope);

        var redelivered = ObjectMother.Envelope();
        redelivered.Id = envelope.Id;
        redelivered.Destination = redeliveryDestinationFor(envelope);
        redelivered.Status = EnvelopeStatus.Incoming;

        await thePersistence.Inbox.StoreIncomingAsync(redelivered);

        var counts = await thePersistence.Admin.FetchCountsAsync();

        counts.Handled.ShouldBe(1, "Guard: the handled row is still there, so the insert above was a genuine same-identity redelivery.");
        counts.Incoming.ShouldBe(1, "The partitioned key does not reject the redelivery: the handled row sits in another partition.");

        (await statusesForAsync(envelope.Id)).ShouldBe(["Handled", "Incoming"]);
    }

    /// <summary>
    /// GH-4216. The worst of the three sibling statements GH-4209 reproduced and deliberately left alone.
    /// <c>_markEnvelopeAsHandledById</c> matched the identity with no status predicate and SET status, so
    /// retiring a redelivered row was itself a cross-partition move onto the key the retained handled row
    /// already held. The row could not be retired AT ALL: it stayed Incoming, owned by the node that had
    /// already processed it, with nothing left to try.
    /// </summary>
    [Fact]
    public async Task a_redelivered_row_can_be_retired_when_the_identity_is_already_handled()
    {
        var envelope = ObjectMother.Envelope();
        envelope.Status = EnvelopeStatus.Incoming;

        await thePersistence.Inbox.StoreIncomingAsync(envelope);
        await thePersistence.Inbox.MarkIncomingEnvelopeAsHandledAsync(envelope);

        var redelivered = ObjectMother.Envelope();
        redelivered.Id = envelope.Id;
        redelivered.Destination = redeliveryDestinationFor(envelope);
        redelivered.Status = EnvelopeStatus.Incoming;

        await thePersistence.Inbox.StoreIncomingAsync(redelivered);

        // Guard: the pair the partitioned key permits, which is the state this statement has to survive.
        (await statusesForAsync(envelope.Id)).ShouldBe(["Handled", "Incoming"]);

        await thePersistence.Inbox.MarkIncomingEnvelopeAsHandledAsync(redelivered);

        // The incoming copy is gone rather than stranded. The retained handled row is what serves the
        // KeepAfterMessageHandling dedup window, and it was already there.
        (await statusesForAsync(envelope.Id)).ShouldBe(["Handled"]);
    }

    /// <summary>
    /// The batched mark-as-handled coalesces completions into one statement, so it has to carry the same
    /// shape -- otherwise a coalesced retire strands exactly the rows the single-envelope path now retires.
    /// </summary>
    [Fact]
    public async Task the_batched_retire_survives_the_same_pair()
    {
        var envelope = ObjectMother.Envelope();
        envelope.Status = EnvelopeStatus.Incoming;

        await thePersistence.Inbox.StoreIncomingAsync(envelope);
        await thePersistence.Inbox.MarkIncomingEnvelopeAsHandledAsync(envelope);

        var redelivered = ObjectMother.Envelope();
        redelivered.Id = envelope.Id;
        redelivered.Destination = redeliveryDestinationFor(envelope);
        redelivered.Status = EnvelopeStatus.Incoming;

        await thePersistence.Inbox.StoreIncomingAsync(redelivered);

        await thePersistence.Inbox.MarkIncomingEnvelopeAsHandledAsync([redelivered]);

        (await statusesForAsync(envelope.Id)).ShouldBe(["Handled"]);
    }

    /// <summary>
    /// GH-4216. <c>ScheduleExecutionAsync</c> matched the identity with no status predicate and then SET
    /// status, so under partitioning it moved EVERY row for the identity into the scheduled partition --
    /// including a retained handled row it was never given. Two rows onto one scheduled key is a 23505, and
    /// the reschedule that raised it is a retry that never happens. Resurrecting a completed message is the
    /// worse half of it; the collision is only what made it visible.
    /// </summary>
    [Fact]
    public async Task a_retry_can_be_parked_while_a_handled_row_is_retained()
    {
        var handled = ObjectMother.Envelope();
        handled.Status = EnvelopeStatus.Incoming;

        await thePersistence.Inbox.StoreIncomingAsync(handled);
        await thePersistence.Inbox.MarkIncomingEnvelopeAsHandledAsync(handled);

        var retry = dueScheduledEnvelope();
        retry.Id = handled.Id;
        retry.Destination = redeliveryDestinationFor(handled);

        await thePersistence.Inbox.StoreIncomingAsync(retry);

        // Guard: the pair the partitioned key permits, which is the state this statement has to survive.
        (await statusesForAsync(handled.Id)).ShouldBe(["Handled", "Incoming"]);

        await thePersistence.Inbox.ScheduleExecutionAsync(retry);

        (await statusesForAsync(handled.Id)).ShouldBe(["Handled", "Scheduled"],
            "The incoming copy is parked as the retry, and the retained handled row stays where it is.");
    }

    /// <summary>
    /// The reschedule path has the same statement and the same exposure, plus a fallback that inserts when
    /// the update affects nothing -- which would land straight on the handled row's key.
    /// </summary>
    [Fact]
    public async Task a_reschedule_for_retry_can_be_parked_while_a_handled_row_is_retained()
    {
        var handled = ObjectMother.Envelope();
        handled.Status = EnvelopeStatus.Incoming;

        await thePersistence.Inbox.StoreIncomingAsync(handled);
        await thePersistence.Inbox.MarkIncomingEnvelopeAsHandledAsync(handled);

        var retry = dueScheduledEnvelope();
        retry.Id = handled.Id;
        retry.Destination = redeliveryDestinationFor(handled);

        await thePersistence.Inbox.StoreIncomingAsync(retry);
        await thePersistence.Inbox.RescheduleExistingEnvelopeForRetryAsync(retry);

        (await statusesForAsync(handled.Id)).ShouldBe(["Handled", "Scheduled"]);
    }

    /// <summary>
    /// GH-4216, the second way the same statement failed: an earlier retry already sits in the scheduled
    /// partition -- exactly the state RescheduleExistingEnvelopeForRetryAsync exists to service -- so moving
    /// the incoming copy onto that key collided with it. One row survives instead, and it is the scheduled
    /// one, because that is the copy the poller will actually run.
    /// </summary>
    [Fact]
    public async Task a_second_reschedule_converges_on_the_one_scheduled_row()
    {
        var envelope = dueScheduledEnvelope();
        await thePersistence.Inbox.StoreIncomingAsync(envelope);

        envelope.Attempts = 1;
        await thePersistence.Inbox.RescheduleExistingEnvelopeForRetryAsync(envelope);

        (await statusesForAsync(envelope.Id)).ShouldBe(["Scheduled"]);

        // A broker redelivery lands in the incoming partition alongside the parked retry -- the pair the
        // partitioned key permits.
        var redelivered = ObjectMother.Envelope();
        redelivered.Id = envelope.Id;
        redelivered.Destination = redeliveryDestinationFor(envelope);
        redelivered.Status = EnvelopeStatus.Incoming;
        redelivered.ScheduledTime = DateTimeOffset.UtcNow.AddMinutes(-1);

        await thePersistence.Inbox.StoreIncomingAsync(redelivered);
        (await statusesForAsync(envelope.Id)).ShouldBe(["Incoming", "Scheduled"]);

        redelivered.Attempts = 2;
        await thePersistence.Inbox.RescheduleExistingEnvelopeForRetryAsync(redelivered);

        (await statusesForAsync(envelope.Id)).ShouldBe(["Scheduled"],
            "The redundant incoming copy is discarded rather than moved onto the key the scheduled row holds.");

        var counts = await thePersistence.Admin.FetchCountsAsync();
        counts.Scheduled.ShouldBe(1, "Exactly one scheduled row survives for the identity.");
        counts.Incoming.ShouldBe(0);
    }

    [Fact]
    public async Task the_unqualified_promotion_statement_raises_a_unique_violation()
    {
        var envelope = await createDuplicatedIdentityAsync();

        var cancellation = TestContext.Current.CancellationToken;

        await using var conn = new NpgsqlConnection(Servers.PostgresConnectionString);
        await conn.OpenAsync(cancellation);

        await using var command = conn.CreateCommand();
        command.CommandText =
            $"update {schemaName}.{DatabaseConstants.IncomingTable} set owner_id = @owner, status = 'Incoming' where id = ANY(@ids)";
        command.Parameters.AddWithValue("owner", 1);
        command.Parameters.AddWithValue("ids", new[] { envelope.Id });

        var exception = await Should.ThrowAsync<PostgresException>(async () =>
            await command.ExecuteNonQueryAsync(cancellation));

        exception.SqlState.ShouldBe(PostgresErrorCodes.UniqueViolation);
    }

    [Fact]
    public async Task one_duplicated_row_does_not_block_unrelated_scheduled_messages()
    {
        var duplicated = await createDuplicatedIdentityAsync();

        var valid = dueScheduledEnvelope();
        valid.Destination = new Uri("stub://valid");
        await thePersistence.Inbox.StoreIncomingAsync(valid);
        await thePersistence.Inbox.ScheduleExecutionAsync(valid);

        var captured = await pollAsync();

        captured.ShouldNotBeEmpty("The poll must have won the scheduled-job advisory lock and actually run.");

        captured.ShouldContain(x => x.Id == valid.Id,
            "An unrelated valid scheduled message must still be dispatched.");

        captured.ShouldNotContain(x => x.Id == duplicated.Id,
            "The superseded scheduled copy must not be dispatched alongside the incoming row it duplicates.");
    }

    [Fact]
    public async Task several_duplicated_rows_are_discarded_in_one_poll()
    {
        var first = await createDuplicatedIdentityAsync();
        var second = await createDuplicatedIdentityAsync();

        var valid = dueScheduledEnvelope();
        valid.Destination = new Uri("stub://valid");
        await thePersistence.Inbox.StoreIncomingAsync(valid);
        await thePersistence.Inbox.ScheduleExecutionAsync(valid);

        var captured = await pollAsync();

        captured.ShouldContain(x => x.Id == valid.Id,
            "The unrelated valid scheduled message must still be dispatched.");

        captured.ShouldNotContain(x => x.Id == first.Id || x.Id == second.Id,
            "Every superseded scheduled copy in the batch must be dropped, not just the first one found.");

        var counts = await thePersistence.Admin.FetchCountsAsync();

        counts.Scheduled.ShouldBe(0, "Both superseded rows must be discarded in the same poll.");
        counts.Incoming.ShouldBe(3, "The two surviving redeliveries and the promoted valid message remain.");

        (await statusesForAsync(first.Id)).ShouldBe(["Incoming"]);
        (await statusesForAsync(second.Id)).ShouldBe(["Incoming"]);
    }

    [Fact]
    public async Task promotion_converges_on_a_single_row()
    {
        var envelope = await createDuplicatedIdentityAsync();

        var captured = await pollAsync();

        captured.ShouldBeEmpty("Nothing was promotable, so the superseded row must not reach dispatch either.");

        var counts = await thePersistence.Admin.FetchCountsAsync();

        counts.Scheduled.ShouldBe(0, "The superseded scheduled row must be discarded rather than left to poison every later poll.");
        counts.Incoming.ShouldBe(1, "The surviving incoming row must be the only copy of the identity left.");

        (await statusesForAsync(envelope.Id)).ShouldBe(["Incoming"]);

        (await survivingAttemptsAsync(envelope.Id)).ShouldBe(RedeliveryAttempts,
            "The surviving row must keep the attempt count it was stored with rather than the one the discarded retry had reached.");
    }

    [Fact]
    public async Task a_scheduled_retry_is_discarded_when_the_identity_is_already_handled()
    {
        var handled = ObjectMother.Envelope();
        handled.Status = EnvelopeStatus.Incoming;

        await thePersistence.Inbox.StoreIncomingAsync(handled);
        await thePersistence.Inbox.MarkIncomingEnvelopeAsHandledAsync(handled);

        var scheduled = dueScheduledEnvelope();
        scheduled.Id = handled.Id;
        scheduled.Destination = redeliveryDestinationFor(handled);

        await thePersistence.Inbox.StoreIncomingAsync(scheduled);

        // GH-4216: this used to be a raw-SQL helper, because ScheduleExecutionAsync could not build this pair
        // under IdAndDestination -- it matched the identity with no status predicate, so under partitioning it
        // dragged the handled row into the scheduled partition too and hit 23505. That is fixed, so the
        // precondition is now built by the real call and is one production can actually reach.
        await thePersistence.Inbox.ScheduleExecutionAsync(scheduled);

        var captured = await pollAsync();

        captured.ShouldNotContain(x => x.Id == handled.Id,
            "The identity was already handled, so its pending retry must not be executed a second time.");

        var counts = await thePersistence.Admin.FetchCountsAsync();

        counts.Handled.ShouldBe(1, "The discard is scoped to the scheduled partition, so the handled row stays where it is.");
        counts.Incoming.ShouldBe(0, "Promoting past the handled row would leave a row that can never be marked handled.");
        counts.Scheduled.ShouldBe(0, "The superseded retry is discarded rather than left to re-fail on every later poll.");

        (await statusesForAsync(handled.Id)).ShouldBe(["Handled"]);
    }

    protected async Task<int> ownerOfAsync(Guid id, Uri destination)
    {
        var cancellation = TestContext.Current.CancellationToken;

        await using var conn = new NpgsqlConnection(Servers.PostgresConnectionString);
        await conn.OpenAsync(cancellation);

        await using var command = conn.CreateCommand();
        command.CommandText =
            $"select {DatabaseConstants.OwnerId} from {schemaName}.{DatabaseConstants.IncomingTable} " +
            $"where id = @id and {DatabaseConstants.ReceivedAt} = @destination";
        command.Parameters.AddWithValue("id", id);
        command.Parameters.AddWithValue("destination", destination.ToString());

        var value = await command.ExecuteScalarAsync(cancellation);

        value.ShouldNotBeNull("The row under test must still exist.");

        return (int)value;
    }

    protected async Task<List<string>> statusesForAsync(Guid id)
    {
        var cancellation = TestContext.Current.CancellationToken;

        await using var conn = new NpgsqlConnection(Servers.PostgresConnectionString);
        await conn.OpenAsync(cancellation);

        await using var command = conn.CreateCommand();
        command.CommandText =
            $"select status from {schemaName}.{DatabaseConstants.IncomingTable} where id = @id order by status";
        command.Parameters.AddWithValue("id", id);

        var statuses = new List<string>();

        await using var reader = await command.ExecuteReaderAsync(cancellation);
        while (await reader.ReadAsync(cancellation))
        {
            statuses.Add(reader.GetString(0));
        }

        return statuses;
    }

    private async Task<int> survivingAttemptsAsync(Guid id)
    {
        var cancellation = TestContext.Current.CancellationToken;

        await using var conn = new NpgsqlConnection(Servers.PostgresConnectionString);
        await conn.OpenAsync(cancellation);

        await using var command = conn.CreateCommand();
        command.CommandText =
            $"select {DatabaseConstants.Attempts} from {schemaName}.{DatabaseConstants.IncomingTable} where id = @id and status = '{EnvelopeStatus.Incoming}'";
        command.Parameters.AddWithValue("id", id);

        var value = await command.ExecuteScalarAsync(cancellation);

        value.ShouldNotBeNull("An incoming row must survive for the identity under test.");

        return (int)value;
    }

    private async Task<Envelope> createDuplicatedIdentityAsync()
    {
        var envelope = dueScheduledEnvelope();
        await thePersistence.Inbox.StoreIncomingAsync(envelope);

        envelope.Attempts = RetryAttempts;
        await thePersistence.Inbox.RescheduleExistingEnvelopeForRetryAsync(envelope);

        var redelivered = ObjectMother.Envelope();
        redelivered.Id = envelope.Id;
        redelivered.Destination = redeliveryDestinationFor(envelope);
        redelivered.Status = EnvelopeStatus.Incoming;
        redelivered.Attempts = RedeliveryAttempts;

        await thePersistence.Inbox.StoreIncomingAsync(redelivered);

        return envelope;
    }

    protected async Task<List<Envelope>> pollAsync()
    {
        var captured = new List<Envelope>();
        var spyRuntime = Substitute.For<IWolverineRuntime>();
        spyRuntime
            .EnqueueDirectlyAsync(Arg.Do<IReadOnlyList<Envelope>>(es => captured.AddRange(es)))
            .Returns(ValueTask.CompletedTask);

        var durabilitySettings = theHost!.Services.GetRequiredService<DurabilitySettings>();

        await ((IMessageDatabase)thePersistence).PollForScheduledMessagesAsync(
            spyRuntime, NullLogger.Instance, durabilitySettings, CancellationToken.None);

        return captured;
    }
}

[Collection("marten")]
public class Bug_partitioned_inbox_scheduled_promotion_by_id_and_destination
    : PartitionedInboxScheduledPromotionContext
{
    protected override MessageIdentity identityStyle => MessageIdentity.IdAndDestination;
    protected override string schemaName => "partitioned_promotion_id_and_destination";

    protected override Uri redeliveryDestinationFor(Envelope original) => original.Destination!;

    [Fact]
    public async Task a_shared_id_at_another_destination_is_a_different_identity()
    {
        var scheduled = dueScheduledEnvelope();
        await thePersistence.Inbox.StoreIncomingAsync(scheduled);
        await thePersistence.Inbox.ScheduleExecutionAsync(scheduled);

        var elsewhere = ObjectMother.Envelope();
        elsewhere.Id = scheduled.Id;
        elsewhere.Destination = new Uri("stub://elsewhere");
        elsewhere.Status = EnvelopeStatus.Incoming;

        await thePersistence.Inbox.StoreIncomingAsync(elsewhere);

        var ownerBefore = await ownerOfAsync(elsewhere.Id, elsewhere.Destination);

        var captured = await pollAsync();

        captured.ShouldContain(x => x.Id == scheduled.Id && x.Destination == scheduled.Destination,
            "Under IdAndDestination the incoming row at another destination is a different identity, so it must not supersede the scheduled message.");

        var counts = await thePersistence.Admin.FetchCountsAsync();

        counts.Scheduled.ShouldBe(0, "The scheduled row was promoted rather than discarded as superseded.");
        counts.Incoming.ShouldBe(2, "Both identities survive: the promoted one and the unrelated row sharing its id.");

        (await ownerOfAsync(elsewhere.Id, elsewhere.Destination)).ShouldBe(ownerBefore,
            "Matching on the id alone would let the promotion claim ownership of a row the poller never selected.");
    }

    [Fact]
    public async Task a_scheduled_sibling_at_another_destination_is_left_alone()
    {
        var due = dueScheduledEnvelope();
        await thePersistence.Inbox.StoreIncomingAsync(due);
        await thePersistence.Inbox.ScheduleExecutionAsync(due);

        var later = ObjectMother.Envelope();
        later.Id = due.Id;
        later.Destination = new Uri("stub://elsewhere");
        later.Status = EnvelopeStatus.Incoming;
        later.ScheduledTime = DateTimeOffset.UtcNow.AddHours(1);

        await thePersistence.Inbox.StoreIncomingAsync(later);
        await thePersistence.Inbox.ScheduleExecutionAsync(later);

        var captured = await pollAsync();

        captured.ShouldContain(x => x.Destination == due.Destination,
            "The due scheduled message must still be promoted and dispatched.");

        captured.ShouldNotContain(x => x.Destination == later.Destination,
            "The sibling is not due yet, so the poller must not have selected it.");

        (await statusesForAsync(due.Id)).ShouldBe(["Incoming", "Scheduled"],
            "Both rows sit in the scheduled partition, so only the identity clause can keep the promotion off the sibling.");
    }
}

[Collection("marten")]
public class Bug_partitioned_inbox_scheduled_promotion_by_id_only
    : PartitionedInboxScheduledPromotionContext
{
    protected override MessageIdentity identityStyle => MessageIdentity.IdOnly;
    protected override string schemaName => "partitioned_promotion_id_only";

    protected override Uri redeliveryDestinationFor(Envelope original) => new("stub://redelivered");
}
