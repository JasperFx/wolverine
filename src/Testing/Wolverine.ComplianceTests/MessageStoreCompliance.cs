using JasperFx.Core;
using JasperFx.Core.Reflection;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using JasperFx.Resources;
using NSubstitute;
using Shouldly;
using Wolverine.Persistence.Durability;
using Wolverine.Persistence.Durability.DeadLetterManagement;
using Wolverine.Persistence.Durability.ScheduledMessageManagement;
using Wolverine.RDBMS;
using Wolverine.Runtime;
using Wolverine.Runtime.Agents;
using Wolverine.Transports;
using Xunit;

namespace Wolverine.ComplianceTests;

public abstract class MessageStoreCompliance : IAsyncLifetime
{
    public IHost theHost { get; private set; } = null!;
    protected IMessageStore thePersistence = null!;
    
    public abstract Task<IHost> BuildCleanHost();
    
    public async ValueTask InitializeAsync()
    {
        theHost = await BuildCleanHost();

        await theHost.ResetResourceState();

        thePersistence = theHost.Services.GetRequiredService<IMessageStore>();
    }

    public async ValueTask DisposeAsync()
    {
        await theHost.StopAsync();
        theHost.Dispose();

        await afterHostDisposedAsync();
    }

    /// <summary>
    /// Runs after each test's host is gone. A store that leaves its tables in a shape another test class
    /// cannot migrate from (IdAndDestination's wider inbox key, with rows sharing an id) cleans up here.
    /// </summary>
    protected virtual Task afterHostDisposedAsync() => Task.CompletedTask;
    
    [Fact]
    public async Task get_counts()
    {
        var thePersistor = theHost.Get<IMessageStore>();

        var list = new List<Envelope>();

        // 10 incoming
        for (var i = 0; i < 10; i++)
        {
            var envelope = ObjectMother.Envelope();
            envelope.Status = EnvelopeStatus.Incoming;

            list.Add(envelope);
        }

        await thePersistor.Inbox.StoreIncomingAsync(list.ToArray());


        // 7 scheduled
        list.Clear();
        for (var i = 0; i < 7; i++)
        {
            var envelope = ObjectMother.Envelope();
            envelope.Status = EnvelopeStatus.Scheduled;

            list.Add(envelope);
        }

        await thePersistor.Inbox.StoreIncomingAsync(list.ToArray());


        // 3 outgoing
        list.Clear();
        for (var i = 0; i < 3; i++)
        {
            var envelope = ObjectMother.Envelope();
            envelope.Status = EnvelopeStatus.Outgoing;

            list.Add(envelope);
        }

        foreach (var envelope in list)
        {
            await thePersistor.Outbox.StoreOutgoingAsync(envelope, 0);
        }

        var counts = await thePersistor.Admin.FetchCountsAsync();

        counts.Incoming.ShouldBe(10);
        counts.Scheduled.ShouldBe(7);
        counts.Outgoing.ShouldBe(3);
    }

    [Fact]
    public async Task store_a_single_incoming_envelope()
    {
        var envelope = ObjectMother.Envelope();
        envelope.Status = EnvelopeStatus.Incoming;
        envelope.SentAt = ((DateTimeOffset)DateTime.Today).ToUniversalTime();

        await thePersistence.Inbox.StoreIncomingAsync(envelope);

        var stored = (await thePersistence.Admin.AllIncomingAsync()).Single();

        stored.Id.ShouldBe(envelope.Id);
        stored.OwnerId.ShouldBe(envelope.OwnerId);
        stored.Status.ShouldBe(envelope.Status);

        stored.SentAt.ShouldBe(envelope.SentAt);
    }

    [Fact]
    public async Task incoming_exists()
    {
        var envelope = ObjectMother.Envelope();
        envelope.Status = EnvelopeStatus.Incoming;
        envelope.SentAt = ((DateTimeOffset)DateTime.Today).ToUniversalTime();
        
        (await thePersistence.Inbox.ExistsAsync(envelope, CancellationToken.None)).ShouldBeFalse();
        
        await thePersistence.Inbox.StoreIncomingAsync(envelope);
        
        (await thePersistence.Inbox.ExistsAsync(envelope, CancellationToken.None)).ShouldBeTrue();
    }
    
    // GH-5049. IMessageInbox.IsHandledAsync: the push receiver claims a message id with an Incoming row before running
    // the handler, and has to tell that claim apart from a message the inbox has finished with

    private static Envelope incomingEnvelope(EnvelopeStatus status = EnvelopeStatus.Incoming)
    {
        var envelope = ObjectMother.Envelope();
        envelope.Status = status;
        envelope.SentAt = ((DateTimeOffset)DateTime.Today).ToUniversalTime();
        return envelope;
    }

    [Fact]
    public async Task is_handled_is_false_with_no_row()
    {
        (await thePersistence.Inbox.IsHandledAsync(incomingEnvelope(), CancellationToken.None)).ShouldBeFalse();
    }

    [Fact]
    public async Task an_incoming_row_is_only_handled_once_it_is_marked_handled()
    {
        var envelope = incomingEnvelope();

        await thePersistence.Inbox.StoreIncomingAsync(envelope);
        (await thePersistence.Inbox.IsHandledAsync(envelope, CancellationToken.None)).ShouldBeFalse();

        await thePersistence.Inbox.MarkIncomingEnvelopeAsHandledAsync(envelope);
        (await thePersistence.Inbox.IsHandledAsync(envelope, CancellationToken.None)).ShouldBeTrue();
    }

    [Fact]
    public async Task a_row_stored_as_handled_is_handled()
    {
        // As the push receiver and the inbox deduplication record a message they have finished with
        var envelope = incomingEnvelope(EnvelopeStatus.Handled);

        await thePersistence.Inbox.StoreIncomingAsync(envelope);

        (await thePersistence.Inbox.IsHandledAsync(envelope, CancellationToken.None)).ShouldBeTrue();
    }

    [Fact]
    public async Task a_scheduled_row_is_not_handled()
    {
        var envelope = incomingEnvelope(EnvelopeStatus.Scheduled);
        envelope.ScheduledTime = DateTimeOffset.UtcNow.AddMinutes(5);

        await thePersistence.Inbox.StoreIncomingAsync(envelope);

        (await thePersistence.Inbox.IsHandledAsync(envelope, CancellationToken.None)).ShouldBeFalse();
    }

    [Fact]
    public async Task a_dead_lettered_message_is_not_handled()
    {
        var envelope = incomingEnvelope();
        await thePersistence.Inbox.StoreIncomingAsync(envelope);

        await thePersistence.Inbox.MoveToDeadLetterStorageAsync(envelope, new InvalidOperationException("Boom"));

        (await thePersistence.Inbox.IsHandledAsync(envelope, CancellationToken.None)).ShouldBeFalse();
    }

    [Fact]
    public async Task is_handled_uses_the_same_identity_rule_as_exists()
    {
        var handled = incomingEnvelope(EnvelopeStatus.Handled);
        await thePersistence.Inbox.StoreIncomingAsync(handled);

        // The same id received at another address
        var elsewhere = incomingEnvelope();
        elsewhere.Id = handled.Id;
        elsewhere.Destination = new Uri("stub://elsewhere");

        var exists = await thePersistence.Inbox.ExistsAsync(elsewhere, CancellationToken.None);
        var isHandled = await thePersistence.Inbox.IsHandledAsync(elsewhere, CancellationToken.None);

        // Whatever the store's own identity rule finds, both answer the same. (Under IdOnly a relational store finds
        // the same id at another address, while Cosmos DB, which partitions by destination, does not)
        isHandled.ShouldBe(exists);

        // Under IdAndDestination it is a different message on every store
        if (identityStyle() == MessageIdentity.IdAndDestination)
        {
            isHandled.ShouldBeFalse();
        }
    }

    [Fact]
    public async Task store_a_single_incoming_envelope_that_is_handled()
    {
        // This is for cases where you're only persisting the record for idempotency checks
        
        var envelope = ObjectMother.Envelope();
        envelope.Status = EnvelopeStatus.Handled;
        envelope.SentAt = ((DateTimeOffset)DateTime.Today).ToUniversalTime();

        await thePersistence.Inbox.StoreIncomingAsync(envelope);

        var stored = (await thePersistence.Admin.AllIncomingAsync()).Single();
        
        // This is the important part
        stored.Data!.Length.ShouldBe(0);
        stored.Destination.ShouldBe(envelope.Destination);

        stored.Id.ShouldBe(envelope.Id);
        stored.OwnerId.ShouldBe(envelope.OwnerId);
        stored.Status.ShouldBe(envelope.Status);
    }

    [Fact]
    public async Task store_a_single_incoming_envelope_that_is_a_duplicate()
    {
        var envelope = ObjectMother.Envelope();
        envelope.Destination = new Uri("stub://incoming");
        envelope.Status = EnvelopeStatus.Incoming;

        await thePersistence.Inbox.StoreIncomingAsync(envelope);

        await Should.ThrowAsync<DuplicateIncomingEnvelopeException>(async () =>
        {
            await thePersistence.Inbox.StoreIncomingAsync(envelope);
        });
    }

    /// <summary>
    /// GH-5059. Under <see cref="MessageIdentity.IdOnly"/> a message's identity is its id alone, so the same id
    /// received at another address is the same message, and storing it again is a duplicate. Skipped under
    /// IdAndDestination, where it is a different message
    /// </summary>
    [Fact]
    public async Task under_id_only_the_same_id_at_another_destination_is_a_duplicate()
    {
        if (identityStyle() != MessageIdentity.IdOnly) return;

        var envelope = ObjectMother.Envelope();
        envelope.Destination = new Uri("stub://incoming");
        envelope.Status = EnvelopeStatus.Incoming;
        await thePersistence.Inbox.StoreIncomingAsync(envelope);

        var elsewhere = ObjectMother.Envelope();
        elsewhere.Id = envelope.Id;
        elsewhere.Destination = new Uri("stub://elsewhere");
        elsewhere.Status = EnvelopeStatus.Incoming;

        (await thePersistence.Inbox.ExistsAsync(elsewhere, CancellationToken.None)).ShouldBeTrue();

        await Should.ThrowAsync<DuplicateIncomingEnvelopeException>(async () =>
        {
            await thePersistence.Inbox.StoreIncomingAsync(elsewhere);
        });
    }

    [Fact]
    public async Task bulk_store_intra_batch_duplicate_reports_only_actual_duplicates()
    {
        var existing = ObjectMother.Envelope();
        existing.Destination = new Uri("stub://incoming-bulk-dup");
        existing.Status = EnvelopeStatus.Incoming;
        await thePersistence.Inbox.StoreIncomingAsync(existing);

        var fresh1 = ObjectMother.Envelope();
        fresh1.Destination = existing.Destination;
        fresh1.Status = EnvelopeStatus.Incoming;

        var fresh2 = ObjectMother.Envelope();
        fresh2.Destination = existing.Destination;
        fresh2.Status = EnvelopeStatus.Incoming;

        var batch = new[] { fresh1, existing, fresh2 };

        var ex = await Should.ThrowAsync<DuplicateIncomingEnvelopeException>(
            () => thePersistence.Inbox.StoreIncomingAsync(batch));

        // Only the actually-existing envelope is reported as a duplicate.
        // Fresh envelopes must NOT appear in Duplicates — otherwise DurableReceiver
        // would route them straight to listener.CompleteAsync and the handler would
        // never run for legitimate messages.
        ex.Duplicates.Count.ShouldBe(1);
        ex.Duplicates.Single().Id.ShouldBe(existing.Id);
    }

    /// <summary>
    /// GH-5123. The same envelope twice in ONE batch, with no row for it yet: a broker redelivery after an outage
    /// (or the outbox resending a publish it never saw confirmed) puts both copies in one micro-batch. The batched
    /// insert hits the primary key, the existence check finds nothing -- the duplicate is inside the batch, not in
    /// the table -- and the RDBMS stores rethrew the raw provider exception. The receiver then mistook a duplicate
    /// for an unavailable inbox and paused the listener, and MultiTenantedMessageStore tripped the tenant write
    /// brake, which deferred both copies back to the broker in a loop. RavenDB already reported this as a
    /// duplicate (its own test of the same name); this holds every store to it.
    /// </summary>
    [Fact]
    public async Task bulk_store_with_intra_batch_duplicate_throws_DuplicateIncomingEnvelopeException()
    {
        var envelope = ObjectMother.Envelope();
        envelope.Destination = new Uri("stub://incoming-intra-batch");
        envelope.Status = EnvelopeStatus.Incoming;

        await Should.ThrowAsync<DuplicateIncomingEnvelopeException>(
            () => thePersistence.Inbox.StoreIncomingAsync(new[] { envelope, envelope }));
    }

    /// <summary>
    /// GH-5123. When a batch insert reports a duplicate, DurableReceiver re-runs EVERY envelope of the batch through
    /// the one-at-a-time path, which stores each one again unless it is marked WasPersistedInInbox. So a store may
    /// either roll the whole batch back, or mark the envelopes it did commit, but never leave a fresh envelope
    /// committed and unmarked: its second store reads as a duplicate, and a duplicate is settled with the broker
    /// without ever reaching a handler. Oracle inserted row by row and committed the survivors, unmarked.
    /// </summary>
    [Fact]
    public async Task a_batch_with_a_duplicate_never_leaves_a_fresh_envelope_stored_but_unmarked()
    {
        var existing = ObjectMother.Envelope();
        existing.Destination = new Uri("stub://incoming-bulk-partial");
        existing.Status = EnvelopeStatus.Incoming;
        await thePersistence.Inbox.StoreIncomingAsync(existing);

        var fresh = ObjectMother.Envelope();
        fresh.Destination = existing.Destination;
        fresh.Status = EnvelopeStatus.Incoming;

        var copy = ObjectMother.Envelope();
        copy.Id = existing.Id;
        copy.Destination = existing.Destination;
        copy.Status = EnvelopeStatus.Incoming;

        await Should.ThrowAsync<DuplicateIncomingEnvelopeException>(
            () => thePersistence.Inbox.StoreIncomingAsync(new[] { fresh, copy }));

        if (await thePersistence.Inbox.ExistsAsync(fresh, CancellationToken.None))
        {
            fresh.WasPersistedInInbox.ShouldBeTrue(
                "the store committed this envelope, so the receiver must not store it a second time");
        }
    }

    [Fact]
    public async Task store_a_single_outgoing_envelope()
    {
        var envelope = ObjectMother.Envelope();
        envelope.Status = EnvelopeStatus.Outgoing;
        envelope.SentAt = ((DateTimeOffset)DateTime.Today).ToUniversalTime();

        await thePersistence.Outbox.StoreOutgoingAsync(envelope, 5890);

        var stored = (await thePersistence.Admin.AllOutgoingAsync())
            .Single();

        stored.Id.ShouldBe(envelope.Id);
        stored.OwnerId.ShouldBe(5890);
        stored.Status.ShouldBe(envelope.Status);
        stored.SentAt.ShouldBe(envelope.SentAt);
    }

    /// <summary>
    /// GH-4319 added StoreOutgoingAsync(IReadOnlyList&lt;Envelope&gt;, int) as a DEFAULT interface
    /// method, so a store that overrides it and a store that does not must be indistinguishable from
    /// the outside. GH-4369 then gave Oracle, RavenDB and Cosmos DB real batch implementations. This
    /// test is what holds them to the single-envelope path's behaviour -- every store runs it,
    /// overridden or not.
    /// </summary>
    [Fact]
    public async Task store_a_batch_of_outgoing_envelopes()
    {
        var envelopes = new List<Envelope>();
        for (var i = 0; i < 10; i++)
        {
            var envelope = ObjectMother.Envelope();
            envelope.Status = EnvelopeStatus.Outgoing;
            envelope.SentAt = ((DateTimeOffset)DateTime.Today).ToUniversalTime();
            envelopes.Add(envelope);
        }

        await thePersistence.Outbox.StoreOutgoingAsync(envelopes, 5890);

        var stored = (await thePersistence.Admin.AllOutgoingAsync()).ToArray();

        stored.Length.ShouldBe(envelopes.Count);
        stored.Select(x => x.Id).OrderBy(x => x).ShouldBe(envelopes.Select(x => x.Id).OrderBy(x => x));
        stored.ShouldAllBe(x => x.OwnerId == 5890);
    }

    /// <summary>
    /// The batch has to be a fast path, never a different path. A caller that hands over one envelope
    /// gets exactly what the single-envelope overload would have written -- this is also the shape a
    /// coalescer produces constantly, since a lone write never waits for company.
    /// </summary>
    [Fact]
    public async Task store_a_batch_of_one_outgoing_envelope()
    {
        var envelope = ObjectMother.Envelope();
        envelope.Status = EnvelopeStatus.Outgoing;
        envelope.SentAt = ((DateTimeOffset)DateTime.Today).ToUniversalTime();

        await thePersistence.Outbox.StoreOutgoingAsync(new[] { envelope }, 5890);

        var stored = (await thePersistence.Admin.AllOutgoingAsync()).Single();

        stored.Id.ShouldBe(envelope.Id);
        stored.OwnerId.ShouldBe(5890);
        stored.Status.ShouldBe(envelope.Status);
        stored.SentAt.ShouldBe(envelope.SentAt);
    }

    /// <summary>
    /// GH-4371. <c>WasPersistedInOutbox</c> is not decoration: the database-queue senders
    /// (PostgreSQL, SQL Server, MySQL, SQLite, Oracle) branch on it to decide between MOVING the row
    /// from outgoing into the queue table in one transaction and writing the queue row separately and
    /// deleting outgoing afterwards. Oracle never set it, so its move branch was unreachable for as
    /// long as it existed -- and nothing in the suite noticed, because nothing held the stores to the
    /// contract. This is that check. Every store runs it, including the ones with no queue transport
    /// today: a store that has written the row and reports otherwise is simply wrong, and the next
    /// queue transport should not have to rediscover that.
    /// </summary>
    [Fact]
    public async Task storing_outgoing_envelopes_marks_them_as_persisted_in_the_outbox()
    {
        var single = ObjectMother.Envelope();
        single.Status = EnvelopeStatus.Outgoing;
        single.WasPersistedInOutbox.ShouldBeFalse("precondition: a fresh envelope has not been stored");

        await thePersistence.Outbox.StoreOutgoingAsync(single, 111);
        single.WasPersistedInOutbox.ShouldBeTrue();

        var batch = new List<Envelope>();
        for (var i = 0; i < 3; i++)
        {
            var envelope = ObjectMother.Envelope();
            envelope.Status = EnvelopeStatus.Outgoing;
            batch.Add(envelope);
        }

        await thePersistence.Outbox.StoreOutgoingAsync(batch, 111);
        batch.ShouldAllBe(x => x.WasPersistedInOutbox);
    }

    [Fact]
    public async Task store_an_empty_batch_of_outgoing_envelopes_is_a_no_op()
    {
        await thePersistence.Outbox.StoreOutgoingAsync(Array.Empty<Envelope>(), 5890);

        (await thePersistence.Admin.AllOutgoingAsync()).ShouldBeEmpty();
    }

    [Fact]
    public async Task mark_envelope_as_handled()
    {
        var envelope = ObjectMother.Envelope();

        await thePersistence.Inbox.StoreIncomingAsync(envelope);

        await thePersistence.Inbox.MarkIncomingEnvelopeAsHandledAsync(envelope);

        var counts = await thePersistence.Admin.FetchCountsAsync();

        counts.Incoming.ShouldBe(0);
        counts.Scheduled.ShouldBe(0);
        counts.Handled.ShouldBe(1);
    }

    [Fact]
    public async Task mark_several_envelopes_as_handled()
    {
        var envelope1 = ObjectMother.Envelope();
        await thePersistence.Inbox.StoreIncomingAsync(envelope1);
        
        var envelope2 = ObjectMother.Envelope();
        await thePersistence.Inbox.StoreIncomingAsync(envelope2);
        
        var envelope3 = ObjectMother.Envelope();
        await thePersistence.Inbox.StoreIncomingAsync(envelope3);
        
        var envelope4 = ObjectMother.Envelope();
        await thePersistence.Inbox.StoreIncomingAsync(envelope4);
        
        var envelope5 = ObjectMother.Envelope();
        
        await thePersistence.Inbox.StoreIncomingAsync(envelope5);
        
        await thePersistence.Inbox.MarkIncomingEnvelopeAsHandledAsync([envelope1, envelope2, envelope3, envelope4]);
        await Task.Delay(5000);
        var counts = await thePersistence.Admin.FetchCountsAsync();

        counts.Incoming.ShouldBe(1);
        counts.Scheduled.ShouldBe(0);
        counts.Handled.ShouldBe(4);
    }

    /// <summary>
    /// GH-4701. The mark-as-handled that transactional middleware (EF Core's <c>EfCoreEnvelopeTransaction</c>) issues
    /// on the application's own connection and transaction. Runs on every store and identity shape, including the
    /// partitioned PostgreSQL inbox.
    /// </summary>
    [Fact]
    public virtual async Task mark_envelope_as_handled_in_transaction()
    {
        if (thePersistence is not IMessageDatabase database) return;

        var envelope = ObjectMother.Envelope();
        envelope.Status = EnvelopeStatus.Incoming;
        await thePersistence.Inbox.StoreIncomingAsync(envelope);

        await markAsHandledInTransactionAsync(database, envelope);

        var counts = await thePersistence.Admin.FetchCountsAsync();
        counts.Incoming.ShouldBe(0);
        counts.Handled.ShouldBe(1);
    }

    /// <summary>
    /// GH-4701. Under <see cref="MessageIdentity.IdAndDestination"/> a row sharing the id at another destination is a
    /// different message: the copy of a fanned-out message whose own handler may not have run yet. Marking it handled
    /// here means a node that stops before that handler runs loses the copy for good, because the durability agent only
    /// recovers Incoming rows. The same identity rule as <c>MarkAsHandledSql</c>, and the twin of the GH-4216 promotion
    /// tests. Skipped under IdOnly, where a shared id at another destination is by definition the same identity.
    /// </summary>
    [Fact]
    public virtual async Task mark_as_handled_in_transaction_leaves_a_shared_id_at_another_destination_alone()
    {
        if (thePersistence is not IMessageDatabase database) return;
        if (identityStyle() != MessageIdentity.IdAndDestination) return;

        var handled = ObjectMother.Envelope();
        handled.Status = EnvelopeStatus.Incoming;
        await thePersistence.Inbox.StoreIncomingAsync(handled);

        var elsewhere = ObjectMother.Envelope();
        elsewhere.Id = handled.Id;
        elsewhere.Destination = new Uri("stub://elsewhere");
        elsewhere.Status = EnvelopeStatus.Incoming;
        await thePersistence.Inbox.StoreIncomingAsync(elsewhere);

        await markAsHandledInTransactionAsync(database, handled);

        var rows = await thePersistence.Admin.AllIncomingAsync();

        rows.Single(x => x.Id == handled.Id && x.Destination == handled.Destination)
            .Status.ShouldBe(EnvelopeStatus.Handled);

        rows.Single(x => x.Id == elsewhere.Id && x.Destination == elsewhere.Destination)
            .Status.ShouldBe(EnvelopeStatus.Incoming,
                "Matching on the id alone retires a copy whose own handler has not run yet, and a Handled row is never recovered.");
    }

    /// <summary>
    /// GH-4739. A Handled row is kept only for idempotency and nothing recovers it, so it has no owner -- the same as
    /// a row inserted already Handled (<c>Envelope.ForPersistedHandled</c>). Left owned by the node that handled it,
    /// every later release of that node's ownership (listener drain, node departure, orphan sweep) rewrites the
    /// retained rows, and they fill the owner index the release statements seek.
    ///
    /// GH-4784/GH-4785: this is a contract on every store, not only the relational ones -- the assertion reads
    /// through <c>Admin.AllIncomingAsync()</c> and needs nothing of <c>IMessageDatabase</c>. Only the
    /// in-transaction sibling below is genuinely relational.
    /// </summary>
    [Fact]
    public virtual async Task mark_envelope_as_handled_releases_its_owner()
    {
        var envelope = ObjectMother.Envelope();
        await thePersistence.Inbox.StoreIncomingAsync(envelope);

        await thePersistence.Inbox.MarkIncomingEnvelopeAsHandledAsync(envelope);

        await theOwnerShouldBeReleasedAsync(envelope);
    }

    /// <summary>
    /// GH-4739. The batched mark-as-handled builds its own statement, so it has to release the owner as well.
    /// </summary>
    [Fact]
    public virtual async Task mark_several_envelopes_as_handled_releases_their_owner()
    {
        var envelope1 = ObjectMother.Envelope();
        var envelope2 = ObjectMother.Envelope();
        await thePersistence.Inbox.StoreIncomingAsync(envelope1);
        await thePersistence.Inbox.StoreIncomingAsync(envelope2);

        await thePersistence.Inbox.MarkIncomingEnvelopeAsHandledAsync([envelope1, envelope2]);

        await theOwnerShouldBeReleasedAsync(envelope1);
        await theOwnerShouldBeReleasedAsync(envelope2);
    }

    /// <summary>
    /// GH-4739. The in-transaction mark-as-handled (EF Core, and the statement Marten, Polecat and Fisher queue).
    /// This one keeps its <c>IMessageDatabase</c> guard: it takes a <c>DbConnection</c> and <c>DbTransaction</c>,
    /// so there is nothing for a document store to run.
    /// </summary>
    [Fact]
    public virtual async Task mark_envelope_as_handled_in_transaction_releases_its_owner()
    {
        if (thePersistence is not IMessageDatabase database) return;

        var envelope = ObjectMother.Envelope();
        await thePersistence.Inbox.StoreIncomingAsync(envelope);

        await markAsHandledInTransactionAsync(database, envelope);

        await theOwnerShouldBeReleasedAsync(envelope);
    }

    private async Task theOwnerShouldBeReleasedAsync(Envelope envelope)
    {
        var stored = (await thePersistence.Admin.AllIncomingAsync())
            .Single(x => x.Id == envelope.Id && x.Destination == envelope.Destination);

        stored.Status.ShouldBe(EnvelopeStatus.Handled);
        stored.OwnerId.ShouldBe(TransportConstants.AnyNode,
            "A Handled row is never recovered, so leaving it owned only makes every release of that owner rewrite it.");
    }

    /// <summary>
    /// Mark the envelope handled the way <c>EfCoreEnvelopeTransaction.CommitAsync</c> does: on a caller-owned
    /// connection, inside a caller-owned transaction, then commit.
    /// </summary>
    protected async Task markAsHandledInTransactionAsync(IMessageDatabase database, Envelope envelope)
    {
        await using var conn = await database.DataSource.OpenConnectionAsync();
        await using var tx = await conn.BeginTransactionAsync();

        await database.MarkIncomingEnvelopeAsHandledInTransactionAsync(conn, tx, envelope,
            DateTimeOffset.UtcNow.AddMinutes(5), CancellationToken.None);

        await tx.CommitAsync();
    }

    [Fact]
    public async Task schedule_execution()
    {
        var list = new List<Envelope>();

        for (var i = 0; i < 10; i++)
        {
            var envelope = ObjectMother.Envelope();
            envelope.Status = EnvelopeStatus.Incoming;

            list.Add(envelope);
        }

        await thePersistence.Inbox.StoreIncomingAsync(list.ToArray());


        list[5].ScheduledTime = DateTimeOffset.Now.AddMinutes(5);

        list[7].ScheduledTime = DateTimeOffset.Now.AddMinutes(5);
        list[9].ScheduledTime = DateTimeOffset.Now.AddMinutes(5);

        await thePersistence.Inbox.ScheduleExecutionAsync(list[5]);
        await thePersistence.Inbox.ScheduleExecutionAsync(list[7]);
        await thePersistence.Inbox.ScheduleExecutionAsync(list[9]);

        var stored = await thePersistence.Admin.AllIncomingAsync();
        stored.Count(x => x.Status == EnvelopeStatus.Incoming).ShouldBe(7);
        stored.Count(x => x.Status == EnvelopeStatus.Scheduled).ShouldBe(3);

        stored.Single(x => x.Id == list[5].Id).ScheduledTime.HasValue.ShouldBeTrue();
        stored.Single(x => x.Id == list[7].Id).ScheduledTime.HasValue.ShouldBeTrue();
        stored.Single(x => x.Id == list[9].Id).ScheduledTime.HasValue.ShouldBeTrue();
    }


    /// <summary>
    /// GH-4216, workstream 2, decided: a retry booking that lands AFTER the message was marked handled must not
    /// resurrect it. ScheduleExecutionAsync runs from a RetryBlock in DurableReceiver, so it is free to land after
    /// mark-as-handled for the same identity, and every store used to flip the retained Handled row straight back
    /// to Scheduled with its body intact -- a second, fully executable copy of a message that already completed.
    /// </summary>
    [Fact]
    public async Task a_late_retry_booking_does_not_resurrect_a_handled_row()
    {
        var envelope = ObjectMother.Envelope();
        envelope.Status = EnvelopeStatus.Incoming;

        await thePersistence.Inbox.StoreIncomingAsync(envelope);
        await thePersistence.Inbox.MarkIncomingEnvelopeAsHandledAsync(envelope);

        envelope.ScheduledTime = DateTimeOffset.UtcNow.AddMinutes(5);
        await thePersistence.Inbox.ScheduleExecutionAsync(envelope);

        var counts = await thePersistence.Admin.FetchCountsAsync();
        counts.Handled.ShouldBe(1);
        counts.Scheduled.ShouldBe(0);
        counts.Incoming.ShouldBe(0);
    }

    /// <summary>
    /// The other entry point for a late retry booking. On the relational stores its "no row matched" fallback is an
    /// insert, which collided with the retained Handled row's primary key; on the document stores the whole method
    /// was insert-only. Either way the answer is the same: the message already completed, the retry is discarded.
    /// </summary>
    [Fact]
    public async Task rescheduling_for_retry_after_the_message_was_handled_is_discarded()
    {
        var envelope = ObjectMother.Envelope();
        envelope.Status = EnvelopeStatus.Incoming;

        await thePersistence.Inbox.StoreIncomingAsync(envelope);
        await thePersistence.Inbox.MarkIncomingEnvelopeAsHandledAsync(envelope);

        envelope.ScheduledTime = DateTimeOffset.UtcNow.AddMinutes(5);
        envelope.Attempts = 2;
        await thePersistence.Inbox.RescheduleExistingEnvelopeForRetryAsync(envelope);

        var counts = await thePersistence.Admin.FetchCountsAsync();
        counts.Handled.ShouldBe(1);
        counts.Scheduled.ShouldBe(0);
        counts.Incoming.ShouldBe(0);
    }

    /// <summary>
    /// GH-4216. The normal case RescheduleExistingEnvelopeForRetryAsync exists for (GH-2462 / GH-2823): the row is
    /// already in the inbox and is moved to Scheduled in place. RavenDb and CosmosDb used to throw
    /// DuplicateIncomingEnvelopeException here, because their implementation was a bare insert.
    /// </summary>
    [Fact]
    public async Task rescheduling_for_retry_moves_an_existing_incoming_row_to_scheduled()
    {
        var envelope = ObjectMother.Envelope();
        envelope.Status = EnvelopeStatus.Incoming;

        await thePersistence.Inbox.StoreIncomingAsync(envelope);

        envelope.ScheduledTime = DateTimeOffset.UtcNow.AddMinutes(5);
        envelope.Attempts = 2;
        await thePersistence.Inbox.RescheduleExistingEnvelopeForRetryAsync(envelope);

        var stored = await thePersistence.Admin.AllIncomingAsync();
        var row = stored.Single(x => x.Id == envelope.Id);
        row.Status.ShouldBe(EnvelopeStatus.Scheduled);
        row.ScheduledTime.HasValue.ShouldBeTrue();
        row.Attempts.ShouldBe(2);
    }

    /// <summary>
    /// ...and when nothing is in the inbox yet (ProcessInline retry #1, a buffered scheduled publish), the row is
    /// inserted as Scheduled.
    /// </summary>
    [Fact]
    public async Task rescheduling_for_retry_inserts_when_no_row_exists()
    {
        var envelope = ObjectMother.Envelope();
        envelope.ScheduledTime = DateTimeOffset.UtcNow.AddMinutes(5);

        await thePersistence.Inbox.RescheduleExistingEnvelopeForRetryAsync(envelope);

        var stored = await thePersistence.Admin.AllIncomingAsync();
        stored.Single(x => x.Id == envelope.Id).Status.ShouldBe(EnvelopeStatus.Scheduled);
    }

    [Fact]
    public async Task discard_and_reassign_outgoing()
    {
        var list = new List<Envelope>();

        for (var i = 0; i < 10; i++)
        {
            var envelope = ObjectMother.Envelope();
            envelope.Status = EnvelopeStatus.Outgoing;

            list.Add(envelope);
        }

        foreach (var envelope in list)
        {
            await thePersistence.Outbox.StoreOutgoingAsync(envelope, 111);
        }

        var toDiscard = new[] { list[2], list[3], list[7] };
        var toReassign = new[] { list[1], list[4], list[6] };

        await thePersistence.Outbox.DiscardAndReassignOutgoingAsync(toDiscard, toReassign, 444);

        var stored = await thePersistence.Admin.AllOutgoingAsync();
        stored.Count.ShouldBe(7);

        stored.Any(x => x.Id == list[2].Id).ShouldBeFalse();
        stored.Any(x => x.Id == list[3].Id).ShouldBeFalse();
        stored.Any(x => x.Id == list[7].Id).ShouldBeFalse();

        stored.Single(x => x.Id == list[1].Id).OwnerId.ShouldBe(444);
        stored.Single(x => x.Id == list[4].Id).OwnerId.ShouldBe(444);
        stored.Single(x => x.Id == list[6].Id).OwnerId.ShouldBe(444);
    }
    
    [Fact]
    public async Task delete_dead_letter_message_by_id()
    {
        var unReplayableEnvelope = ObjectMother.Envelope();
        var replayableEnvelope = ObjectMother.Envelope();
        await thePersistence.Inbox.StoreIncomingAsync(unReplayableEnvelope);
        await thePersistence.Inbox.StoreIncomingAsync(replayableEnvelope);

        var divideByZeroException = new DivideByZeroException("Kaboom!");
        var applicationException = new ApplicationException("Kaboom!");
        await thePersistence.Inbox.MoveToDeadLetterStorageAsync(unReplayableEnvelope, divideByZeroException);
        await thePersistence.Inbox.MoveToDeadLetterStorageAsync(replayableEnvelope, applicationException);

        await thePersistence
            .DeadLetters
            .DiscardAsync(new DeadLetterEnvelopeQuery([replayableEnvelope.Id]), CancellationToken.None);

        var counts = await thePersistence.Admin.FetchCountsAsync();

        counts.DeadLetter.ShouldBe(1);
        counts.Incoming.ShouldBe(0);
        counts.Scheduled.ShouldBe(0);
        counts.Handled.ShouldBe(0);
    }

    [Fact]
    public async Task load_incoming_counts()
    {
        var random = new Random();

        var localOne = "local://one".ToUri();
        var localTwo = "local://two".ToUri();

        var list = new List<Envelope>();
        for (var i = 0; i < 100; i++)
        {
            var envelope = ObjectMother.Envelope();
            envelope.Destination = TransportConstants.DurableLocalUri;

            list.Add(envelope);

            if (random.Next(0, 10) > 6)
            {
                envelope.OwnerId = TransportConstants.AnyNode;
            }
            else
            {
                envelope.OwnerId = 5;
            }

            if (random.Next(0, 10) > 4)
            {
                envelope.Destination = localOne;
            }
            else
            {
                envelope.Destination = localTwo;
            }

            if (random.Next(0, 10) > 3)
            {
                envelope.Status = EnvelopeStatus.Incoming;
            }
            else
            {
                envelope.Status = EnvelopeStatus.Handled;
            }
        }

        await thePersistence.Inbox.StoreIncomingAsync(list);

        var counts1 = await thePersistence.LoadPageOfGloballyOwnedIncomingAsync(localOne, 1000);
        var counts2 = await thePersistence.LoadPageOfGloballyOwnedIncomingAsync(localTwo, 1000);


        counts1.Count.ShouldBe(list.Count(x =>
            x.OwnerId == TransportConstants.AnyNode && x.Status == EnvelopeStatus.Incoming &&
            x.Destination == localOne));

        counts2.Count.ShouldBe(list.Count(x =>
            x.OwnerId == TransportConstants.AnyNode && x.Status == EnvelopeStatus.Incoming &&
            x.Destination == localTwo));
    }

    [Fact]
    public async Task fetch_incoming_by_owner_and_address()
    {
        var random = new Random();

        var localOne = "local://one".ToUri();
        var localTwo = "local://two".ToUri();

        var list = new List<Envelope>();
        for (var i = 0; i < 100; i++)
        {
            var envelope = ObjectMother.Envelope();
            list.Add(envelope);

            if (random.Next(0, 10) > 6)
            {
                envelope.OwnerId = TransportConstants.AnyNode;
            }
            else
            {
                envelope.OwnerId = 5;
            }

            if (random.Next(0, 10) > 4)
            {
                envelope.Destination = localOne;
            }
            else
            {
                envelope.Destination = localTwo;
            }

            if (random.Next(0, 10) > 3)
            {
                envelope.Status = EnvelopeStatus.Incoming;
            }
            else
            {
                envelope.Status = EnvelopeStatus.Handled;
            }
        }

        await thePersistence.Inbox.StoreIncomingAsync(list);

        var limit = list.Count(x =>
            x.OwnerId == TransportConstants.AnyNode && x.Status == EnvelopeStatus.Incoming &&
            x.Destination == localOne) - 1;
        var one = await thePersistence.LoadPageOfGloballyOwnedIncomingAsync(localOne, limit);
        foreach (var envelope in one)
        {
            envelope.Destination.ShouldBe(localOne);
            envelope.OwnerId.ShouldBe(TransportConstants.AnyNode);
            envelope.Status.ShouldBe(EnvelopeStatus.Incoming);
        }

        one.Count.ShouldBe(limit);
    }

    [Fact]
    public async Task delete_a_single_outgoing_envelope()
    {
        var list = new List<Envelope>();

        for (var i = 0; i < 10; i++)
        {
            var envelope = ObjectMother.Envelope();
            envelope.Status = EnvelopeStatus.Outgoing;

            list.Add(envelope);
        }

        foreach (var envelope in list)
        {
            await thePersistence.Outbox.StoreOutgoingAsync(envelope, 111);
        }

        var toDelete = list[5];

        await thePersistence.Outbox.DeleteOutgoingAsync(toDelete);

        var stored = await thePersistence.Admin.AllOutgoingAsync();
        stored.Count.ShouldBe(9);

        stored.Any(x => x.Id == toDelete.Id).ShouldBeFalse();
    }

    [Fact]
    public async Task store_multiple_incoming_envelopes()
    {
        var list = new List<Envelope>();

        for (var i = 0; i < 10; i++)
        {
            var envelope = ObjectMother.Envelope();
            envelope.Status = EnvelopeStatus.Incoming;

            list.Add(envelope);
        }

        await thePersistence.Inbox.StoreIncomingAsync(list.ToArray());

        var stored = await thePersistence.Admin.AllIncomingAsync();

        list.Select(x => x.Id).OrderBy(x => x)
            .ShouldHaveTheSameElementsAs(stored.Select(x => x.Id).OrderBy(x => x));
    }
    
    [Fact]
    public async Task store_multiple_outgoing_envelopes()
    {
        await thePersistence.Admin.ClearAllAsync();

        var list = new List<Envelope>();

        for (var i = 0; i < 10; i++)
        {
            var envelope = ObjectMother.Envelope();
            envelope.Status = EnvelopeStatus.Outgoing;

            list.Add(envelope);
        }

        foreach (var envelope in list) await thePersistence.Outbox.StoreOutgoingAsync(envelope, 111);

        var stored = await thePersistence.Admin.AllOutgoingAsync();

        list.Select(x => x.Id).OrderBy(x => x)
            .ShouldHaveTheSameElementsAs(stored.Select(x => x.Id).OrderBy(x => x));

        stored.Each(x => x.OwnerId.ShouldBe(111));
    }

    [Fact]
    public async Task move_to_dead_letter_storage()
    {
        var list = new List<Envelope>();

        for (var i = 0; i < 10; i++)
        {
            var envelope = ObjectMother.Envelope();
            envelope.Status = EnvelopeStatus.Incoming;

            list.Add(envelope);
        }

        await thePersistence.Inbox.StoreIncomingAsync(list.ToArray());


        var ex = new DivideByZeroException("Kaboom!");

        var report2 = new ErrorReport(list[2], ex);
        var report3 = new ErrorReport(list[3], ex);
        var report4 = new ErrorReport(list[4], ex);

        await thePersistence.Inbox.MoveToDeadLetterStorageAsync(report2.Envelope, report2.Exception);
        await thePersistence.Inbox.MoveToDeadLetterStorageAsync(report3.Envelope, report2.Exception);
        await thePersistence.Inbox.MoveToDeadLetterStorageAsync(report4.Envelope, report2.Exception);

        var stored = await thePersistence.Admin.AllIncomingAsync();

        stored.Count.ShouldBe(7);

        stored.Any(x => x.Id == list[2].Id).ShouldBeFalse();
        stored.Any(x => x.Id == list[3].Id).ShouldBeFalse();
        stored.Any(x => x.Id == list[4].Id).ShouldBeFalse();
    }
    
    [Fact]
    public async Task load_dead_letter_envelopes_with_limit()
    {
        var list = new List<Envelope>();

        for (var i = 0; i < 10; i++)
        {
            var envelope = ObjectMother.Envelope();
            envelope.Status = EnvelopeStatus.Incoming;

            list.Add(envelope);
        }

        await thePersistence.Inbox.StoreIncomingAsync(list.ToArray());


        var ex = new DivideByZeroException("Kaboom!");

        var report2 = new ErrorReport(list[2], ex);
        var report3 = new ErrorReport(list[3], ex);
        var report4 = new ErrorReport(list[4], ex);

        await thePersistence.Inbox.MoveToDeadLetterStorageAsync(report2.Envelope, ex);
        await thePersistence.Inbox.MoveToDeadLetterStorageAsync(report3.Envelope, ex);
        await thePersistence.Inbox.MoveToDeadLetterStorageAsync(report4.Envelope, ex);

        var stored = await thePersistence.DeadLetters.QueryAsync(new DeadLetterEnvelopeQuery()
        {
            PageSize = 2
        }, CancellationToken.None);

        stored.Envelopes.Count.ShouldBe(2);
    }

    [Fact]
    public async Task query_dead_letter_envelopes_with_from_and_until()
    {
        var list = new List<Envelope>();

        for (var i = 0; i < 10; i++)
        {
            var envelope = ObjectMother.Envelope();
            envelope.Status = EnvelopeStatus.Incoming;

            list.Add(envelope);
        }

        await thePersistence.Inbox.StoreIncomingAsync(list.ToArray());


        var ex = new DivideByZeroException("Kaboom!");

        var report2 = new ErrorReport(list[2], ex);
        var report3 = new ErrorReport(list[3], ex);
        var report4 = new ErrorReport(list[4], ex);

        await thePersistence.Inbox.MoveToDeadLetterStorageAsync(report2.Envelope, ex);
        await thePersistence.Inbox.MoveToDeadLetterStorageAsync(report3.Envelope, ex);
        await thePersistence.Inbox.MoveToDeadLetterStorageAsync(report4.Envelope, ex);

        var query = new DeadLetterEnvelopeQuery(new TimeRange(DateTimeOffset.Now.AddDays(-1),
            DateTimeOffset.Now.AddDays(1)));
        
        var result = await thePersistence.DeadLetters.QueryAsync(query, CancellationToken.None);

        result.Envelopes.Count.ShouldBe(3);
    }

    [Fact]
    public async Task load_dead_letter_envelopes_by_exception_type()
    {
        var list = new List<Envelope>();

        for (var i = 0; i < 10; i++)
        {
            var envelope = ObjectMother.Envelope();
            envelope.Status = EnvelopeStatus.Incoming;

            list.Add(envelope);
        }

        await thePersistence.Inbox.StoreIncomingAsync(list.ToArray());


        var ex = new DivideByZeroException("Kaboom!");

        var report2 = new ErrorReport(list[2], ex);
        var report3 = new ErrorReport(list[3], ex);
        var report4 = new ErrorReport(list[4], ex);

        await thePersistence.Inbox.MoveToDeadLetterStorageAsync(report2.Envelope, ex);
        await thePersistence.Inbox.MoveToDeadLetterStorageAsync(report3.Envelope, ex);
        await thePersistence.Inbox.MoveToDeadLetterStorageAsync(report4.Envelope, ex);


        var stored = await thePersistence.DeadLetters.QueryAsync(
            new()
            {
                ExceptionType = report2.ExceptionType
            }, CancellationToken.None);

        stored.Envelopes.Count.ShouldBe(3);
        stored.Envelopes.ShouldContain(x => x.Envelope.Id == report2.Id);
        stored.Envelopes.ShouldContain(x => x.Envelope.Id == report3.Id);
        stored.Envelopes.ShouldContain(x => x.Envelope.Id == report4.Id);
    }

    [Fact]
    public async Task increment_the_attempt_count_of_incoming_envelope()
    {
        var envelope = ObjectMother.Envelope();
        envelope.Status = EnvelopeStatus.Incoming;

        await thePersistence.Inbox.StoreIncomingAsync(envelope);

        var prop = ReflectionHelper.GetProperty<Envelope>(x => x.Attempts);
        prop.SetValue(envelope, 3);

        await thePersistence.Inbox.IncrementIncomingEnvelopeAttemptsAsync(envelope);

        var stored = (await thePersistence.Admin.AllIncomingAsync()).Single();
        stored.Attempts.ShouldBe(3);
    }

    [Fact]
    public async Task load_dead_letter_envelope()
    {
        var list = new List<Envelope>();

        for (var i = 0; i < 10; i++)
        {
            var envelope = ObjectMother.Envelope();
            envelope.Status = EnvelopeStatus.Incoming;

            list.Add(envelope);
        }

        await thePersistence.Inbox.StoreIncomingAsync(list.ToArray());


        var ex = new DivideByZeroException("Kaboom!");

        var report2 = new ErrorReport(list[2], ex);
        var report3 = new ErrorReport(list[3], ex);
        var report4 = new ErrorReport(list[4], ex);

        await thePersistence.Inbox.MoveToDeadLetterStorageAsync(report2.Envelope, ex);
        await thePersistence.Inbox.MoveToDeadLetterStorageAsync(report3.Envelope, ex);
        await thePersistence.Inbox.MoveToDeadLetterStorageAsync(report4.Envelope, ex);


        var stored = await thePersistence.DeadLetters.DeadLetterEnvelopeByIdAsync(report2.Id);

        stored.ShouldNotBeNull();

        stored.ExceptionMessage.ShouldBe(report2.ExceptionMessage);
        stored.Envelope.Id.ShouldBe(report2.Id);
        stored.ExceptionType.ShouldBe(report2.ExceptionType);
        stored.Envelope.MessageType.ShouldBe(report2.Envelope.MessageType);
        stored.Envelope.Source.ShouldBe(report2.Envelope.Source);
    }

    [Fact]
    public async Task load_dead_letter_envelopes_by_message_type()
    {
        var list = new List<Envelope>();

        for (var i = 0; i < 10; i++)
        {
            var envelope = ObjectMother.Envelope();
            envelope.Status = EnvelopeStatus.Incoming;

            list.Add(envelope);
        }

        await thePersistence.Inbox.StoreIncomingAsync(list.ToArray());


        var ex = new DivideByZeroException("Kaboom!");

        var report2 = new ErrorReport(list[2], ex);
        var report3 = new ErrorReport(list[3], ex);
        var report4 = new ErrorReport(list[4], ex);

        await thePersistence.Inbox.MoveToDeadLetterStorageAsync(report2.Envelope, ex);
        await thePersistence.Inbox.MoveToDeadLetterStorageAsync(report3.Envelope, ex);
        await thePersistence.Inbox.MoveToDeadLetterStorageAsync(report4.Envelope, ex);


        var stored = await thePersistence.DeadLetters.QueryAsync(
            new()
            {
                MessageType = report2.Envelope.MessageType
            }, CancellationToken.None);

        stored.Envelopes.Count.ShouldBe(3);
        stored.Envelopes.ShouldContain(x => x.Envelope.Id == report2.Id);
        stored.Envelopes.ShouldContain(x => x.Envelope.Id == report3.Id);
        stored.Envelopes.ShouldContain(x => x.Envelope.Id == report4.Id);
    }

    [Fact]
    public async Task delete_multiple_outgoing_envelope()
    {
        var list = new List<Envelope>();

        for (var i = 0; i < 10; i++)
        {
            var envelope = ObjectMother.Envelope();
            envelope.Status = EnvelopeStatus.Outgoing;

            list.Add(envelope);
        }

        foreach (var envelope in list) await thePersistence.Outbox.StoreOutgoingAsync(envelope, 111);

        var toDelete = new[] { list[2], list[3], list[7] };

        await thePersistence.Outbox.DeleteOutgoingAsync(toDelete);

        var stored = await thePersistence.Admin.AllOutgoingAsync();
        stored.Count.ShouldBe(7);

        stored.Any(x => x.Id == list[2].Id).ShouldBeFalse();
        stored.Any(x => x.Id == list[3].Id).ShouldBeFalse();
        stored.Any(x => x.Id == list[7].Id).ShouldBeFalse();
    }

    [Fact]
    public async Task persist_and_load_pin_and_pause_restrictions()
    {
        await thePersistence.Admin.ClearAllAsync();
        
        var restriction1 = new AgentRestriction(Guid.NewGuid(), new Uri("fake://1"), AgentRestrictionType.Pinned, 1);
        var restriction2 = new AgentRestriction(Guid.NewGuid(), new Uri("fake://2"), AgentRestrictionType.Pinned, 2);
        var restriction3 = new AgentRestriction(Guid.NewGuid(), new Uri("fake://3"), AgentRestrictionType.Pinned, 3);
        
        var restriction4 = new AgentRestriction(Guid.NewGuid(), new Uri("fake://4"), AgentRestrictionType.Paused, 0);
        var restriction5 = new AgentRestriction(Guid.NewGuid(), new Uri("fake://5"), AgentRestrictionType.Paused, 0);
        var restriction6 = new AgentRestriction(Guid.NewGuid(), new Uri("fake://6"), AgentRestrictionType.Paused, 0);

        IReadOnlyList<AgentRestriction> restrictions = [restriction1, restriction2, restriction3, restriction4, restriction5, restriction6];
        await thePersistence.Nodes.PersistAgentRestrictionsAsync(
            restrictions,
            CancellationToken.None);

        var state = await thePersistence.Nodes.LoadNodeAgentStateAsync(CancellationToken.None);

        state.Restrictions.Current.OrderBy(x => x.AgentUri.ToString()).ShouldBe(restrictions.OrderBy(x => x.AgentUri.ToString()));
    }
    
    [Fact]
    public async Task persist_and_load_a_restriction_with_a_full_length_agent_uri()
    {
        // GH-4280. The restriction round trip above only ever uses "fake://1" -- eight characters -- so a
        // provider could declare this column varchar(100) and stay green across the whole cross-provider
        // suite. SQL Server did, for a release. ApplyRestrictionsAsync -> PersistAgentRestrictionsAsync is
        // a live write path, so an agent whose URI ran past the column width failed the insert outright.
        await thePersistence.Admin.ClearAllAsync();

        var pinned = new AgentRestriction(Guid.NewGuid(), LongAgentUri.ForAgent(1), AgentRestrictionType.Pinned, 4);
        var paused = new AgentRestriction(Guid.NewGuid(), LongAgentUri.ForAgent(2), AgentRestrictionType.Paused, 0);

        IReadOnlyList<AgentRestriction> restrictions = [pinned, paused];
        await thePersistence.Nodes.PersistAgentRestrictionsAsync(restrictions, CancellationToken.None);

        var state = await thePersistence.Nodes.LoadNodeAgentStateAsync(CancellationToken.None);

        // Byte-identical, not merely present: a silently truncated URI never matches an agent again, and
        // that failure is invisible until an operator wonders why the pin did nothing.
        state.Restrictions.Current.OrderBy(x => x.AgentUri.ToString())
            .ShouldBe(restrictions.OrderBy(x => x.AgentUri.ToString()));
    }

    [Fact]
    public async Task persist_then_overwrite_with_none_restriction_deletes()
    {
        await thePersistence.Admin.ClearAllAsync();
        
        var restriction1 = new AgentRestriction(Guid.NewGuid(), new Uri("fake://1"), AgentRestrictionType.Pinned, 1);
        var restriction2 = new AgentRestriction(Guid.NewGuid(), new Uri("fake://2"), AgentRestrictionType.Pinned, 2);
        var restriction3 = new AgentRestriction(Guid.NewGuid(), new Uri("fake://3"), AgentRestrictionType.Pinned, 3);
        
        var restriction4 = new AgentRestriction(Guid.NewGuid(), new Uri("fake://4"), AgentRestrictionType.Paused, 0);
        var restriction5 = new AgentRestriction(Guid.NewGuid(), new Uri("fake://5"), AgentRestrictionType.Paused, 0);
        var restriction6 = new AgentRestriction(Guid.NewGuid(), new Uri("fake://6"), AgentRestrictionType.Paused, 0);

        IReadOnlyList<AgentRestriction> restrictions = [restriction1, restriction2, restriction3, restriction4, restriction5, restriction6];
        await thePersistence.Nodes.PersistAgentRestrictionsAsync(
            restrictions,
            CancellationToken.None);

        await thePersistence.Nodes.PersistAgentRestrictionsAsync(
        [
            restriction3 with { Type = AgentRestrictionType.None },
            restriction4 with { Type = AgentRestrictionType.None }
        ], CancellationToken.None);

        var state = await thePersistence.Nodes.LoadNodeAgentStateAsync(CancellationToken.None);

        state.Restrictions.Current.OrderBy(x => x.AgentUri.ToString()).ShouldBe([restriction1, restriction2, restriction5, restriction6]);
    }

    [Fact]
    public async Task can_edit_and_replay_dead_letter_envelope()
    {
        var envelope = ObjectMother.Envelope();
        await thePersistence.Inbox.StoreIncomingAsync(envelope);

        var exception = new InvalidOperationException("Test error");
        await thePersistence.Inbox.MoveToDeadLetterStorageAsync(envelope, exception);

        var newBody = new byte[] { 1, 2, 3, 4, 5 };
        await thePersistence.DeadLetters.EditAndReplayAsync(envelope.Id, newBody, CancellationToken.None);

        var deadLetter = await thePersistence.DeadLetters.DeadLetterEnvelopeByIdAsync(envelope.Id);
        deadLetter.ShouldNotBeNull();
        deadLetter.Envelope.Data.ShouldBe(newBody);
        deadLetter.Replayable.ShouldBeTrue();
    }

    [Fact]
    public async Task query_scheduled_messages()
    {
        var scheduledList = new List<Envelope>();
        for (var i = 0; i < 5; i++)
        {
            var envelope = ObjectMother.Envelope();
            envelope.Status = EnvelopeStatus.Scheduled;
            envelope.ScheduledTime = DateTimeOffset.UtcNow.AddMinutes(10 + i);
            scheduledList.Add(envelope);
        }

        await thePersistence.Inbox.StoreIncomingAsync(scheduledList);

        // Also add non-scheduled incoming
        var incomingList = new List<Envelope>();
        for (var i = 0; i < 3; i++)
        {
            var envelope = ObjectMother.Envelope();
            envelope.Status = EnvelopeStatus.Incoming;
            incomingList.Add(envelope);
        }

        await thePersistence.Inbox.StoreIncomingAsync(incomingList);

        var results = await thePersistence.ScheduledMessages.QueryAsync(
            new ScheduledMessageQuery { PageSize = 3 }, CancellationToken.None);

        results.Messages.Count.ShouldBe(3);
        results.TotalCount.ShouldBe(5);
    }

    [Fact]
    public async Task query_scheduled_messages_by_message_type()
    {
        var list = new List<Envelope>();
        for (var i = 0; i < 3; i++)
        {
            var envelope = ObjectMother.Envelope();
            envelope.Status = EnvelopeStatus.Scheduled;
            envelope.ScheduledTime = DateTimeOffset.UtcNow.AddMinutes(10);
            envelope.MessageType = "TypeA";
            list.Add(envelope);
        }

        for (var i = 0; i < 2; i++)
        {
            var envelope = ObjectMother.Envelope();
            envelope.Status = EnvelopeStatus.Scheduled;
            envelope.ScheduledTime = DateTimeOffset.UtcNow.AddMinutes(10);
            envelope.MessageType = "TypeB";
            list.Add(envelope);
        }

        await thePersistence.Inbox.StoreIncomingAsync(list);

        var results = await thePersistence.ScheduledMessages.QueryAsync(
            new ScheduledMessageQuery { MessageType = "TypeA" }, CancellationToken.None);

        results.TotalCount.ShouldBe(3);
        results.Messages.Count.ShouldBe(3);
        results.Messages.ShouldAllBe(m => m.MessageType == "TypeA");
    }

    [Fact]
    public async Task query_scheduled_messages_carries_each_envelopes_tenant()
    {
        var tenanted = ObjectMother.Envelope();
        tenanted.Status = EnvelopeStatus.Scheduled;
        tenanted.ScheduledTime = DateTimeOffset.UtcNow.AddMinutes(10);
        tenanted.TenantId = "tenant-a";

        var untenanted = ObjectMother.Envelope();
        untenanted.Status = EnvelopeStatus.Scheduled;
        untenanted.ScheduledTime = DateTimeOffset.UtcNow.AddMinutes(11);
        untenanted.TenantId = null;

        await thePersistence.Inbox.StoreIncomingAsync([tenanted, untenanted]);

        var results = await thePersistence.ScheduledMessages.QueryAsync(
            new ScheduledMessageQuery(), CancellationToken.None);

        results.Messages.Single(x => x.Id == tenanted.Id).TenantId.ShouldBe("tenant-a");
        results.Messages.Single(x => x.Id == untenanted.Id).TenantId.ShouldBeNull();
    }

    [Fact]
    public async Task query_scheduled_messages_carries_each_envelopes_saga_id()
    {
        var timeout = ObjectMother.Envelope();
        timeout.Status = EnvelopeStatus.Scheduled;
        timeout.ScheduledTime = DateTimeOffset.UtcNow.AddMinutes(10);
        timeout.SagaId = "saga-1";

        var unrelated = ObjectMother.Envelope();
        unrelated.Status = EnvelopeStatus.Scheduled;
        unrelated.ScheduledTime = DateTimeOffset.UtcNow.AddMinutes(11);
        unrelated.SagaId = null;

        await thePersistence.Inbox.StoreIncomingAsync([timeout, unrelated]);

        var results = await thePersistence.ScheduledMessages.QueryAsync(
            new ScheduledMessageQuery(), CancellationToken.None);

        results.Messages.Single(x => x.Id == timeout.Id).SagaId.ShouldBe("saga-1");
        results.Messages.Single(x => x.Id == unrelated.Id).SagaId.ShouldBeNull();
    }

    [Fact]
    public async Task query_scheduled_messages_by_saga_id_pages_only_the_sagas_messages()
    {
        // The saga's three timeouts are interleaved by execution time with five other scheduled
        // messages, so a page over the raw rows would hold the wrong mix
        var list = new List<Envelope>();
        for (var i = 0; i < 8; i++)
        {
            var envelope = ObjectMother.Envelope();
            envelope.Status = EnvelopeStatus.Scheduled;
            envelope.ScheduledTime = DateTimeOffset.UtcNow.AddMinutes(10 + i);
            envelope.SagaId = i % 3 == 0 ? "saga-1" : "saga-2";
            list.Add(envelope);
        }

        await thePersistence.Inbox.StoreIncomingAsync(list);

        var sagaTimeouts = list.Where(x => x.SagaId == "saga-1").Select(x => x.Id).ToArray();

        var page1 = await thePersistence.ScheduledMessages.QueryAsync(
            new ScheduledMessageQuery { SagaId = "saga-1", PageSize = 2 }, CancellationToken.None);

        page1.TotalCount.ShouldBe(3);
        page1.Messages.Select(x => x.Id).ShouldBe(sagaTimeouts.Take(2));
        page1.Messages.ShouldAllBe(x => x.SagaId == "saga-1");

        var page2 = await thePersistence.ScheduledMessages.QueryAsync(
            new ScheduledMessageQuery { SagaId = "saga-1", PageSize = 2, PageNumber = 2 }, CancellationToken.None);

        page2.TotalCount.ShouldBe(3);
        page2.Messages.Select(x => x.Id).ShouldBe(sagaTimeouts.Skip(2));
    }

    [Fact]
    public async Task cancel_scheduled_messages_by_saga_id()
    {
        var list = new List<Envelope>();
        for (var i = 0; i < 5; i++)
        {
            var envelope = ObjectMother.Envelope();
            envelope.Status = EnvelopeStatus.Scheduled;
            envelope.ScheduledTime = DateTimeOffset.UtcNow.AddMinutes(10 + i);
            envelope.SagaId = i < 2 ? "saga-1" : "saga-2";
            list.Add(envelope);
        }

        await thePersistence.Inbox.StoreIncomingAsync(list);

        await thePersistence.ScheduledMessages.CancelAsync(
            new ScheduledMessageQuery { SagaId = "saga-1" }, CancellationToken.None);

        (await thePersistence.Admin.FetchCountsAsync()).Scheduled.ShouldBe(3);

        // A saga with nothing scheduled cancels nothing, rather than falling through to an unfiltered cancel
        await thePersistence.ScheduledMessages.CancelAsync(
            new ScheduledMessageQuery { SagaId = "no-such-saga" }, CancellationToken.None);

        var remaining = await thePersistence.ScheduledMessages.QueryAsync(
            new ScheduledMessageQuery(), CancellationToken.None);

        remaining.TotalCount.ShouldBe(3);
        remaining.Messages.ShouldAllBe(x => x.SagaId == "saga-2");
    }

    [Fact]
    public async Task cancel_scheduled_message_by_id()
    {
        var list = new List<Envelope>();
        for (var i = 0; i < 3; i++)
        {
            var envelope = ObjectMother.Envelope();
            envelope.Status = EnvelopeStatus.Scheduled;
            envelope.ScheduledTime = DateTimeOffset.UtcNow.AddMinutes(10);
            list.Add(envelope);
        }

        await thePersistence.Inbox.StoreIncomingAsync(list);

        await thePersistence.ScheduledMessages.CancelAsync(
            new ScheduledMessageQuery { MessageIds = [list[1].Id] }, CancellationToken.None);

        var counts = await thePersistence.Admin.FetchCountsAsync();
        counts.Scheduled.ShouldBe(2);
    }

    [Fact]
    public async Task cancel_scheduled_messages_by_message_type()
    {
        var list = new List<Envelope>();
        for (var i = 0; i < 3; i++)
        {
            var envelope = ObjectMother.Envelope();
            envelope.Status = EnvelopeStatus.Scheduled;
            envelope.ScheduledTime = DateTimeOffset.UtcNow.AddMinutes(10);
            envelope.MessageType = "CancelTypeA";
            list.Add(envelope);
        }

        for (var i = 0; i < 2; i++)
        {
            var envelope = ObjectMother.Envelope();
            envelope.Status = EnvelopeStatus.Scheduled;
            envelope.ScheduledTime = DateTimeOffset.UtcNow.AddMinutes(10);
            envelope.MessageType = "CancelTypeB";
            list.Add(envelope);
        }

        await thePersistence.Inbox.StoreIncomingAsync(list);

        await thePersistence.ScheduledMessages.CancelAsync(
            new ScheduledMessageQuery { MessageType = "CancelTypeA" }, CancellationToken.None);

        var counts = await thePersistence.Admin.FetchCountsAsync();
        counts.Scheduled.ShouldBe(2);
    }

    [Fact]
    public async Task reschedule_scheduled_message()
    {
        var envelope = ObjectMother.Envelope();
        envelope.Status = EnvelopeStatus.Scheduled;
        envelope.ScheduledTime = DateTimeOffset.UtcNow.AddMinutes(10);
        await thePersistence.Inbox.StoreIncomingAsync(envelope);

        var newTime = DateTimeOffset.UtcNow.AddHours(5);
        await thePersistence.ScheduledMessages.RescheduleAsync(envelope.Id, newTime, CancellationToken.None);

        var results = await thePersistence.ScheduledMessages.QueryAsync(
            new ScheduledMessageQuery { MessageIds = [envelope.Id] }, CancellationToken.None);

        results.Messages.Count.ShouldBe(1);
        // Verify the time was updated (within a second tolerance)
        results.Messages[0].ScheduledTime!.Value.ShouldBe(newTime.ToUniversalTime(), TimeSpan.FromSeconds(1));
    }

    [Fact]
    public async Task summarize_scheduled_messages()
    {
        var list = new List<Envelope>();
        for (var i = 0; i < 3; i++)
        {
            var envelope = ObjectMother.Envelope();
            envelope.Status = EnvelopeStatus.Scheduled;
            envelope.ScheduledTime = DateTimeOffset.UtcNow.AddMinutes(10);
            envelope.MessageType = "SumTypeA";
            list.Add(envelope);
        }

        for (var i = 0; i < 2; i++)
        {
            var envelope = ObjectMother.Envelope();
            envelope.Status = EnvelopeStatus.Scheduled;
            envelope.ScheduledTime = DateTimeOffset.UtcNow.AddMinutes(10);
            envelope.MessageType = "SumTypeB";
            list.Add(envelope);
        }

        var envelope3 = ObjectMother.Envelope();
        envelope3.Status = EnvelopeStatus.Scheduled;
        envelope3.ScheduledTime = DateTimeOffset.UtcNow.AddMinutes(10);
        envelope3.MessageType = "SumTypeC";
        list.Add(envelope3);

        await thePersistence.Inbox.StoreIncomingAsync(list);

        var counts = await thePersistence.ScheduledMessages.SummarizeAsync("TestService", CancellationToken.None);

        counts.Count.ShouldBeGreaterThanOrEqualTo(3);
        counts.ShouldContain(c => c.MessageType == "SumTypeA" && c.Count == 3);
        counts.ShouldContain(c => c.MessageType == "SumTypeB" && c.Count == 2);
        counts.ShouldContain(c => c.MessageType == "SumTypeC" && c.Count == 1);
    }

    [Fact]
    public async Task move_to_dead_letter_storage_with_null_source()
    {
        var envelope = ObjectMother.Envelope();
        envelope.Source = null;
        envelope.Status = EnvelopeStatus.Incoming;

        await thePersistence.Inbox.StoreIncomingAsync(envelope);

        var ex = new DivideByZeroException("Kaboom!");

        await thePersistence.Inbox.MoveToDeadLetterStorageAsync(envelope, ex);

        var stored = await thePersistence.DeadLetters.DeadLetterEnvelopeByIdAsync(envelope.Id);

        stored.ShouldNotBeNull();
        stored.Envelope.Id.ShouldBe(envelope.Id);
        stored.Envelope.Source.ShouldBeNull();
        stored.ExceptionMessage.ShouldBe("Kaboom!");
        stored.ExceptionType.ShouldBe(typeof(DivideByZeroException).FullName);
    }

    /// <summary>
    /// Contract test for https://github.com/JasperFx/wolverine/issues/2576.
    ///
    /// When a scheduled message is loaded from a store via
    /// <see cref="IMessageDatabase.PollForScheduledMessagesAsync"/>, the
    /// resulting in-memory envelope passed to
    /// <see cref="IWolverineRuntime.EnqueueDirectlyAsync"/> must have its
    /// <c>Store</c> property stamped with the originating store. Without this,
    /// downstream pipeline components (DelegatingMessageInbox,
    /// DurableReceiver._markAsHandled, FlushOutgoingMessagesOnCommit) cannot
    /// route their writes back to the correct store, and ancillary-store rows
    /// get stuck in <c>Incoming</c> status forever because the
    /// "mark as handled" SQL targets the main store instead.
    /// </summary>
    [Fact]
    public virtual async Task scheduled_poll_stamps_envelope_with_originating_store()
    {
        if (thePersistence is not IMessageDatabase database)
        {
            // Non-database stores (e.g. RavenDb, CosmosDb) wire scheduled
            // dispatch through their own durability agents, not through
            // PollForScheduledMessagesAsync. Skip this contract there.
            return;
        }

        // Persist a scheduled envelope into this store's incoming table.
        var envelope = ObjectMother.Envelope();
        envelope.Status = EnvelopeStatus.Incoming;
        envelope.ScheduledTime = DateTimeOffset.UtcNow.AddMinutes(-1); // already due
        await thePersistence.Inbox.StoreIncomingAsync(envelope);
        await thePersistence.Inbox.ScheduleExecutionAsync(envelope);

        // Spy runtime that captures whatever PollForScheduledMessagesAsync
        // hands to EnqueueDirectlyAsync.
        var capturedEnvelopes = new List<Envelope>();
        var spyRuntime = Substitute.For<IWolverineRuntime>();
        spyRuntime
            .EnqueueDirectlyAsync(Arg.Do<IReadOnlyList<Envelope>>(es => capturedEnvelopes.AddRange(es)))
            .Returns(ValueTask.CompletedTask);

        var durabilitySettings = theHost.Services.GetRequiredService<DurabilitySettings>();

        await database.PollForScheduledMessagesAsync(
            spyRuntime, NullLogger.Instance, durabilitySettings, CancellationToken.None);

        capturedEnvelopes.ShouldNotBeEmpty(
            "Expected the just-scheduled envelope to be picked up by the poller.");

        var captured = capturedEnvelopes.SingleOrDefault(x => x.Id == envelope.Id);
        captured.ShouldNotBeNull(
            "Expected the polled envelope to match the one we scheduled.");

        captured.Store.ShouldBe(thePersistence,
            "Polled envelopes must be stamped with the store they came from so " +
            "downstream mark-as-handled / inbox writes route back to the correct store. " +
            "See GH-2576.");
    }

    /// <summary>
    /// Contract test for https://github.com/JasperFx/wolverine/issues/4673.
    ///
    /// The promotion UPDATE sets the row to <c>Incoming</c> owned by this node. The in-memory envelope has
    /// to say the same thing, because it was READ while the row still said <c>Scheduled</c> and nothing
    /// else re-reads it.
    ///
    /// <para>This is not cosmetic. <see cref="Envelope.IsScheduledForLater"/> answers true on
    /// <c>Status</c> ALONE, regardless of the clock, so a stale <c>Scheduled</c> makes
    /// <c>Envelope.MarkReceived</c> re-assert it — and <c>DurableReceiver.receiveOneAsync</c> only enqueues
    /// an envelope whose status is <c>Incoming</c>. The message is then dropped with nothing logged, while
    /// its row sits <c>Incoming</c> under a LIVE owner that no recovery path can reclaim: inbox recovery
    /// takes only <c>owner_id = 0</c>, and the orphan sweep only releases nodes proven dead.</para>
    /// </summary>
    [Fact]
    public virtual async Task scheduled_poll_marks_the_envelope_promoted()
    {
        if (thePersistence is not IMessageDatabase database) return;

        var envelope = ObjectMother.Envelope();
        envelope.Status = EnvelopeStatus.Incoming;
        envelope.ScheduledTime = DateTimeOffset.UtcNow.AddMinutes(-1); // already due
        await thePersistence.Inbox.StoreIncomingAsync(envelope);
        await thePersistence.Inbox.ScheduleExecutionAsync(envelope);

        var capturedEnvelopes = new List<Envelope>();
        var spyRuntime = Substitute.For<IWolverineRuntime>();
        spyRuntime
            .EnqueueDirectlyAsync(Arg.Do<IReadOnlyList<Envelope>>(es => capturedEnvelopes.AddRange(es)))
            .Returns(ValueTask.CompletedTask);

        var durabilitySettings = theHost.Services.GetRequiredService<DurabilitySettings>();

        await database.PollForScheduledMessagesAsync(
            spyRuntime, NullLogger.Instance, durabilitySettings, CancellationToken.None);

        var captured = capturedEnvelopes.SingleOrDefault(x => x.Id == envelope.Id);
        captured.ShouldNotBeNull("Expected the just-scheduled envelope to be picked up by the poller.");

        captured.Status.ShouldBe(EnvelopeStatus.Incoming,
            "A promoted envelope must not still claim to be Scheduled -- IsScheduledForLater keys on " +
            "status alone, so MarkReceived would re-assert it and the message would never execute. " +
            "See GH-4673.");

        captured.OwnerId.ShouldBe(durabilitySettings.AssignedNodeNumber,
            "The promotion UPDATE claimed the row for this node, so the in-memory envelope has to agree. " +
            "See GH-4673.");

        // The envelope is what MarkReceived will be asked about, so assert the actual predicate rather
        // than only the field it reads -- that is the thing that was wrong.
        captured.IsScheduledForLater(DateTimeOffset.UtcNow).ShouldBeFalse(
            "A due, promoted envelope must not report itself as scheduled for later. See GH-4673.");
    }

    /// <summary>
    /// GH-4216, lifted out of GH-4209's PostgreSQL-only suite because neither case is partitioning-specific.
    /// Under <see cref="MessageIdentity.IdAndDestination"/> a row that merely shares an id at a *different*
    /// destination is a different message, and scheduled promotion must treat it as one: it may not be
    /// superseded, and -- the half that bites hardest -- its <c>owner_id</c> may not be reassigned. Any
    /// promotion statement that matches on <c>id</c> alone claims a row the poller never selected, which is
    /// duplicate execution waiting on a node that thinks it owns work it was never handed.
    ///
    /// Skipped under IdOnly, where a shared id at another destination is by definition the same identity and
    /// superseding it is correct.
    /// </summary>
    [Fact]
    public virtual async Task promotion_leaves_a_shared_id_at_another_destination_alone()
    {
        if (thePersistence is not IMessageDatabase database) return;
        if (identityStyle() != MessageIdentity.IdAndDestination) return;

        var scheduled = ObjectMother.Envelope();
        scheduled.Status = EnvelopeStatus.Incoming;
        scheduled.ScheduledTime = DateTimeOffset.UtcNow.AddMinutes(-1); // already due
        await thePersistence.Inbox.StoreIncomingAsync(scheduled);
        await thePersistence.Inbox.ScheduleExecutionAsync(scheduled);

        var elsewhere = ObjectMother.Envelope();
        elsewhere.Id = scheduled.Id;
        elsewhere.Destination = new Uri("stub://elsewhere");
        elsewhere.Status = EnvelopeStatus.Incoming;
        await thePersistence.Inbox.StoreIncomingAsync(elsewhere);

        var ownerBefore = (await thePersistence.Admin.AllIncomingAsync())
            .Single(x => x.Id == elsewhere.Id && x.Destination == elsewhere.Destination).OwnerId;

        var captured = await pollForScheduledMessagesAsync(database);

        captured.ShouldContain(x => x.Id == scheduled.Id && x.Destination == scheduled.Destination,
            "Under IdAndDestination the incoming row at another destination is a different identity, so it must not supersede the scheduled message.");

        var rows = await thePersistence.Admin.AllIncomingAsync();

        rows.Count(x => x.Id == scheduled.Id).ShouldBe(2,
            "Both identities survive: the promoted one and the unrelated row sharing its id.");

        rows.Single(x => x.Id == elsewhere.Id && x.Destination == elsewhere.Destination).OwnerId
            .ShouldBe(ownerBefore,
                "Matching on the id alone would let the promotion claim ownership of a row the poller never selected.");
    }

    /// <summary>
    /// GH-4216, the sibling case: two scheduled rows sharing an id at different destinations, one due and one
    /// not. Only the identity clause can keep the promotion off the row that is not due yet -- a status
    /// predicate cannot, because both rows are Scheduled.
    /// </summary>
    [Fact]
    public virtual async Task promotion_leaves_a_not_yet_due_scheduled_sibling_at_another_destination_alone()
    {
        if (thePersistence is not IMessageDatabase database) return;
        if (identityStyle() != MessageIdentity.IdAndDestination) return;

        var due = ObjectMother.Envelope();
        due.Status = EnvelopeStatus.Incoming;
        due.ScheduledTime = DateTimeOffset.UtcNow.AddMinutes(-1);
        await thePersistence.Inbox.StoreIncomingAsync(due);
        await thePersistence.Inbox.ScheduleExecutionAsync(due);

        var later = ObjectMother.Envelope();
        later.Id = due.Id;
        later.Destination = new Uri("stub://elsewhere");
        later.Status = EnvelopeStatus.Incoming;
        later.ScheduledTime = DateTimeOffset.UtcNow.AddHours(1);
        await thePersistence.Inbox.StoreIncomingAsync(later);
        await thePersistence.Inbox.ScheduleExecutionAsync(later);

        var captured = await pollForScheduledMessagesAsync(database);

        captured.ShouldContain(x => x.Id == due.Id && x.Destination == due.Destination,
            "The due scheduled message must still be promoted and dispatched.");

        captured.ShouldNotContain(x => x.Destination == later.Destination,
            "The sibling is not due yet, so the poller must not have selected it.");

        var rows = await thePersistence.Admin.AllIncomingAsync();

        rows.Single(x => x.Id == due.Id && x.Destination == due.Destination)
            .Status.ShouldBe(EnvelopeStatus.Incoming, "The due row was promoted.");

        rows.Single(x => x.Id == later.Id && x.Destination == later.Destination)
            .Status.ShouldBe(EnvelopeStatus.Scheduled, "The sibling that is not due must be left Scheduled.");
    }

    /// <summary>
    /// The identity shape this store was configured with. Read off the running host rather than overridden
    /// per subclass so an IdAndDestination variant needs nothing beyond its own BuildCleanHost.
    /// </summary>
    protected MessageIdentity identityStyle()
    {
        return theHost.Services.GetRequiredService<DurabilitySettings>().MessageIdentity;
    }

    /// <summary>
    /// Runs one promotion pass and hands back whatever the poller dispatched, which is the only place the
    /// promotion statement's identity matching is externally observable.
    /// </summary>
    protected async Task<List<Envelope>> pollForScheduledMessagesAsync(IMessageDatabase database)
    {
        var captured = new List<Envelope>();
        var spyRuntime = Substitute.For<IWolverineRuntime>();
        spyRuntime
            .EnqueueDirectlyAsync(Arg.Do<IReadOnlyList<Envelope>>(es => captured.AddRange(es)))
            .Returns(ValueTask.CompletedTask);

        var durabilitySettings = theHost.Services.GetRequiredService<DurabilitySettings>();

        await database.PollForScheduledMessagesAsync(
            spyRuntime, NullLogger.Instance, durabilitySettings, CancellationToken.None);

        return captured;
    }

    /// <summary>
    /// GH-5073. An inbox address nothing in the test host listens on. The host runs a real durability agent, and
    /// globally owned rows addressed to a live listener -- ObjectMother's envelopes go to the replies endpoint --
    /// are its to recover, so a poll landing mid-test claims them out from under the claims being tested. CI
    /// caught exactly that on Cosmos DB. The agent skips an address with no listener, so these rows are only
    /// ever claimed by the test.
    /// </summary>
    protected static Uri UnlistenedInboxDestination() => new($"stub://gh5073-{Guid.NewGuid():N}/");

    /// <summary>
    /// GH-5073. Two recovery commands can load the same orphan before either claims it. Only the store that
    /// atomically changes owner_id from AnyNode may return the envelope for enqueueing.
    /// </summary>
    [Fact]
    public async Task CompetingRecoveryPassesOnlyClaimAGloballyOwnedIncomingRowOnce()
    {
        var envelopes = new[] { ObjectMother.Envelope(), ObjectMother.Envelope() };
        var destination = UnlistenedInboxDestination();
        foreach (var envelope in envelopes)
        {
            envelope.Destination = destination;
            envelope.Status = EnvelopeStatus.Incoming;
            envelope.OwnerId = TransportConstants.AnyNode;
        }

        await thePersistence.Inbox.StoreIncomingAsync(envelopes);

        // Both recovery passes read the same page before either attempts to claim it.
        var firstCandidates = await thePersistence.LoadPageOfGloballyOwnedIncomingAsync(destination, 10);
        var secondCandidates = await thePersistence.LoadPageOfGloballyOwnedIncomingAsync(destination, 10);

        foreach (var envelope in envelopes)
        {
            firstCandidates.Select(x => x.Id).ShouldContain(envelope.Id);
            secondCandidates.Select(x => x.Id).ShouldContain(envelope.Id);
        }

        var claims = await Task.WhenAll(
            thePersistence.TryClaimIncomingAsync(45_001, firstCandidates),
            thePersistence.TryClaimIncomingAsync(45_002, secondCandidates));

        var claimed = claims.SelectMany(x => x).ToArray();
        claimed.Length.ShouldBe(envelopes.Length,
            "every envelope in the page should be claimed and returned exactly once");
        claimed.Select(x => x.Id).OrderBy(x => x).ShouldBe(envelopes.Select(x => x.Id).OrderBy(x => x));

        foreach (var envelope in claimed)
        {
            envelope.OwnerId.ShouldBeOneOf(45_001, 45_002);
        }

        var remaining = await thePersistence.LoadPageOfGloballyOwnedIncomingAsync(destination, 10);
        remaining.ShouldBeEmpty();
    }

    /// <summary>
    /// GH-5073 end to end against this store. Two inbox recovery commands for one destination both load the
    /// same page of globally owned rows before either claims it -- what the durability agent does when a poll
    /// issues a second command while the first is still on the agents queue, which runs 20 at a time. Each
    /// row must be handed to the listener by exactly one of them.
    /// </summary>
    [Fact]
    public async Task ConcurrentRecoveryCommandsEnqueueEachInboxRowOnce()
    {
        var envelopes = Enumerable.Range(0, 20).Select(_ => ObjectMother.Envelope()).ToArray();
        var destination = UnlistenedInboxDestination();
        foreach (var envelope in envelopes)
        {
            envelope.Destination = destination;
            envelope.Status = EnvelopeStatus.Incoming;
            envelope.OwnerId = TransportConstants.AnyNode;
        }

        await thePersistence.Inbox.StoreIncomingAsync(envelopes);

        var store = LoadBarrierMessageStore.For(thePersistence, callers: 2);
        var enqueued = new System.Collections.Concurrent.ConcurrentBag<Guid>();

        RecoverIncomingMessagesCommand commandFor(int node)
        {
            var circuit = Substitute.For<IListenerCircuit>();
            circuit.Status.Returns(ListeningStatus.Accepting);
            circuit.QueueCount.Returns(0);
            circuit.Endpoint.Returns(new Wolverine.Transports.Local.LocalQueue("gh5073"));
            circuit.EnqueueDirectlyAsync(Arg.Any<IEnumerable<Envelope>>()).Returns(callInfo =>
            {
                foreach (var envelope in callInfo.Arg<IEnumerable<Envelope>>()) enqueued.Add(envelope.Id);
                return Task.CompletedTask;
            });

            return new RecoverIncomingMessagesCommand(store, new IncomingCount(destination, envelopes.Length),
                circuit, new DurabilitySettings { AssignedNodeNumber = node }, NullLogger.Instance);
        }

        var runtime = Substitute.For<IWolverineRuntime>();
        await Task.WhenAll(
            commandFor(45_001).ExecuteAsync(runtime, CancellationToken.None),
            commandFor(45_002).ExecuteAsync(runtime, CancellationToken.None));

        enqueued.OrderBy(x => x).ShouldBe(envelopes.Select(x => x.Id).OrderBy(x => x),
            "each recovered inbox row should be enqueued exactly once");

        (await thePersistence.LoadPageOfGloballyOwnedIncomingAsync(destination, 100)).ShouldBeEmpty();
    }

    /// <summary>
    /// GH-5073. The page query has no ORDER BY, so two recovery commands are free to load the same rows in
    /// different orders. A claim that locks rows in the order it was handed them deadlocks against a claim
    /// going the other way, and on PostgreSQL, SQL Server and MySQL it did so on most attempts. Competing
    /// claims must neither fail nor double up, whatever order their candidates arrive in.
    /// </summary>
    [Fact]
    public async Task CompetingRecoveryPassesInOppositeOrdersDoNotDeadlock()
    {
        var envelopes = Enumerable.Range(0, 50).Select(_ => ObjectMother.Envelope()).ToArray();
        var destination = UnlistenedInboxDestination();
        foreach (var envelope in envelopes)
        {
            envelope.Destination = destination;
            envelope.Status = EnvelopeStatus.Incoming;
            envelope.OwnerId = TransportConstants.AnyNode;
        }

        await thePersistence.Inbox.StoreIncomingAsync(envelopes);

        for (var round = 0; round < 10; round++)
        {
            await thePersistence.ReassignIncomingAsync(TransportConstants.AnyNode, envelopes);

            var forwards = await thePersistence.LoadPageOfGloballyOwnedIncomingAsync(destination, 100);
            var backwards = (await thePersistence.LoadPageOfGloballyOwnedIncomingAsync(destination, 100))
                .Reverse().ToArray();

            var claims = await Task.WhenAll(
                thePersistence.TryClaimIncomingAsync(45_001, forwards),
                thePersistence.TryClaimIncomingAsync(45_002, backwards));

            claims.SelectMany(x => x).Select(x => x.Id).OrderBy(x => x)
                .ShouldBe(envelopes.Select(x => x.Id).OrderBy(x => x),
                    $"round {round}: every envelope should be claimed by exactly one of the two passes");
        }
    }

    /// <summary>
    /// Sister contract test to <see cref="scheduled_poll_stamps_envelope_with_originating_store"/>:
    /// the inbox-recovery path (which DLQ replay funnels through) must stamp
    /// <c>envelope.Store</c> so downstream mark-as-handled writes route to the
    /// right database.
    ///
    /// Whenever <c>RecoverIncomingMessagesCommand</c> picks up an orphaned
    /// (<c>owner_id == AnyNode</c>) envelope via
    /// <see cref="IMessageStore.LoadPageOfGloballyOwnedIncomingAsync"/>, it
    /// applies <c>envelope.Store ??= _store</c>. Without that stamp, an
    /// ancillary-owned envelope would be marked Handled in the main store and
    /// stay stuck Incoming. The existing fix lives at
    /// <c>RecoverIncomingMessagesCommand:48</c>; this test pins the behavior
    /// down so the GH-2576 fix doesn't accidentally regress it.
    ///
    /// The DLQ replay flow is one producer of orphaned-incoming rows (via
    /// <c>MoveReplayableErrorMessagesToIncomingOperation</c>), but durability
    /// also generates them when nodes crash mid-handle. We exercise the
    /// recovery contract directly by persisting an envelope with
    /// <c>OwnerId == AnyNode</c>, sidestepping any database-specific quirks
    /// in the move-from-dead-letter SQL.
    /// </summary>
    [Fact]
    public virtual async Task orphaned_incoming_recovery_stamps_envelope_with_originating_store()
    {
        if (thePersistence is not IMessageDatabase)
        {
            return;
        }

        // Persist an envelope as if a previous owner crashed mid-handle —
        // status Incoming, owner_id == AnyNode marks it as orphaned and
        // visible to LoadPageOfGloballyOwnedIncomingAsync.
        var envelope = ObjectMother.Envelope();
        envelope.Status = EnvelopeStatus.Incoming;
        envelope.OwnerId = TransportConstants.AnyNode;
        await thePersistence.Inbox.StoreIncomingAsync(envelope);

        // Recovery loop reads the orphaned incoming envelopes back into memory.
        var recovered = await thePersistence.LoadPageOfGloballyOwnedIncomingAsync(
            envelope.Destination!, 100);

        recovered.ShouldNotBeEmpty(
            "Expected the orphaned envelope to be visible to the recovery loader.");

        var match = recovered.SingleOrDefault(x => x.Id == envelope.Id);
        match.ShouldNotBeNull("Expected the recovered envelope to match the one we persisted.");

        // RecoverIncomingMessagesCommand applies envelope.Store ??= _store
        // immediately after this load. Simulate that step here so the contract
        // test captures what the runtime path actually guarantees.
        foreach (var e in recovered)
        {
            e.Store ??= thePersistence;
        }

        match.Store.ShouldBe(thePersistence,
            "Recovered envelopes must carry their originating store so " +
            "mark-as-handled SQL targets the right database. See GH-2318 / GH-2576.");
    }

    #region IListenerStore compliance — see GH-2685

    /// <summary>
    /// Provider opts into the dynamic-listener registry by setting
    /// <c>Durability.EnableDynamicListeners = true</c> in its <see cref="BuildCleanHost"/>.
    /// Tests below short-circuit when <c>Listeners</c> is the no-op store so providers
    /// can adopt the contract incrementally — once a provider opts in, the same suite
    /// validates real persistence end-to-end.
    /// </summary>
    private bool listenersAreSupported => thePersistence.Listeners is not NullListenerStore;

    [Fact]
    public async Task all_listeners_returns_empty_on_a_clean_store()
    {
        if (!listenersAreSupported) return;

        var listeners = await thePersistence.Listeners.AllListenersAsync();
        listeners.ShouldBeEmpty();
    }

    [Fact]
    public async Task register_listener_persists_uri()
    {
        if (!listenersAreSupported) return;

        var uri = new Uri("mqtt://topic/devices/foo/status");
        await thePersistence.Listeners.RegisterListenerAsync(uri);

        var all = await thePersistence.Listeners.AllListenersAsync();
        all.ShouldContain(uri);
    }

    [Fact]
    public async Task register_listener_is_idempotent()
    {
        if (!listenersAreSupported) return;

        var uri = new Uri("mqtt://topic/devices/dup");
        await thePersistence.Listeners.RegisterListenerAsync(uri);
        await thePersistence.Listeners.RegisterListenerAsync(uri); // no-op, no exception

        var all = await thePersistence.Listeners.AllListenersAsync();
        all.Count(x => x == uri).ShouldBe(1);
    }

    [Fact]
    public async Task remove_listener_removes_the_uri()
    {
        if (!listenersAreSupported) return;

        var uri = new Uri("mqtt://topic/devices/removable");
        await thePersistence.Listeners.RegisterListenerAsync(uri);
        await thePersistence.Listeners.RemoveListenerAsync(uri);

        var all = await thePersistence.Listeners.AllListenersAsync();
        all.ShouldNotContain(uri);
    }

    [Fact]
    public async Task remove_listener_is_idempotent_on_unknown_uri()
    {
        if (!listenersAreSupported) return;

        // Should not throw — the registry tolerates removing a uri that was
        // never registered (matches the "register then crash mid-handler"
        // recovery shape the runtime API depends on).
        await thePersistence.Listeners.RemoveListenerAsync(new Uri("mqtt://topic/never-registered"));
    }

    [Fact]
    public async Task register_then_remove_then_re_register_works()
    {
        if (!listenersAreSupported) return;

        var uri = new Uri("mqtt://topic/devices/cycle");
        await thePersistence.Listeners.RegisterListenerAsync(uri);
        await thePersistence.Listeners.RemoveListenerAsync(uri);
        await thePersistence.Listeners.RegisterListenerAsync(uri);

        var all = await thePersistence.Listeners.AllListenersAsync();
        all.ShouldContain(uri);
    }

    [Fact]
    public async Task all_listeners_includes_every_registered_uri()
    {
        if (!listenersAreSupported) return;

        var a = new Uri("mqtt://topic/a");
        var b = new Uri("mqtt://topic/b");
        var c = new Uri("mqtt://topic/c");
        await thePersistence.Listeners.RegisterListenerAsync(a);
        await thePersistence.Listeners.RegisterListenerAsync(b);
        await thePersistence.Listeners.RegisterListenerAsync(c);

        var all = await thePersistence.Listeners.AllListenersAsync();
        all.ShouldContain(a);
        all.ShouldContain(b);
        all.ShouldContain(c);
    }

    #endregion

}
