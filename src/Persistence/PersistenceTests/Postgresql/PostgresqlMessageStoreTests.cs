using IntegrationTests;
using JasperFx.Core;
using JasperFx.Core.Reflection;
using Marten;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Oakton.Resources;
using PersistenceTests.Marten;
using PersistenceTests.Marten.Persistence;
using Shouldly;
using TestingSupport;
using Weasel.Core;
using Wolverine;
using Wolverine.Marten;
using Wolverine.Persistence.Durability;
using Wolverine.Postgresql;
using Wolverine.RDBMS;
using Wolverine.RDBMS.Durability;
using Wolverine.RDBMS.Polling;
using Wolverine.Transports;
using Wolverine.Transports.Tcp;
using Wolverine.Util;
using Xunit;

namespace PersistenceTests.Postgresql;

public class PostgresqlMessageStoreTests : PostgresqlContext, IDisposable, IAsyncLifetime
{
    public IHost theHost = WolverineHost.For(opts =>
    {
        opts.Services.AddMarten(x =>
        {
            x.Connection(Servers.PostgresConnectionString);
            x.DatabaseSchemaName = "receiver";
        }).IntegrateWithWolverine();

        opts.ListenAtPort(2345).UseDurableInbox();
    });

    private IMessageDatabase thePersistence;

    public async Task InitializeAsync()
    {
        var store = theHost.Get<IDocumentStore>();
        await store.Advanced.Clean.CompletelyRemoveAllAsync();
        await theHost.ResetResourceState();

        thePersistence = (IMessageDatabase)theHost.Services.GetRequiredService<IMessageStore>();
    }

    public Task DisposeAsync()
    {
        Dispose();
        return Task.CompletedTask;
    }

    public void Dispose()
    {
        theHost?.Dispose();
    }

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
        var envelope = SqlServer.ObjectMother.Envelope();
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
    public async Task store_a_single_incoming_envelope_that_is_a_duplicate()
    {
        var envelope = SqlServer.ObjectMother.Envelope();
        envelope.Status = EnvelopeStatus.Incoming;

        await thePersistence.Inbox.StoreIncomingAsync(envelope);

        await Should.ThrowAsync<DuplicateIncomingEnvelopeException>(async () =>
        {
            await thePersistence.Inbox.StoreIncomingAsync(envelope);
        });
    }

    [Fact]
    public async Task store_a_single_outgoing_envelope()
    {
        var envelope = SqlServer.ObjectMother.Envelope();
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
    public async Task delete_expired_envelopes()
    {
        var envelope = ObjectMother.Envelope();

        await thePersistence.Inbox.StoreIncomingAsync(envelope);

        await thePersistence.Inbox.MarkIncomingEnvelopeAsHandledAsync(envelope);

        var hourAgo = DateTimeOffset.UtcNow.Add(1.Hours());
        var operation = new DeleteExpiredEnvelopesOperation(new DbObjectName("receiver", DatabaseConstants.IncomingTable), hourAgo);
        var batch = new DatabaseOperationBatch(thePersistence, new IDatabaseOperation[] { operation });
        await theHost.InvokeAsync(batch);

        var counts = await thePersistence.Admin.FetchCountsAsync();

        counts.Incoming.ShouldBe(0);
        counts.Scheduled.ShouldBe(0);
        counts.Handled.ShouldBe(0);
        
        // Now, let's set the keep until in the past
        
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
    public async Task move_replayable_error_messages_to_incoming()
    {
        /*
         * Going to start with two error messages in dead letter queue
         * Mark one as Replayable
         * Run the DurabilityAction
         * Replayable message should be moved back to Inbox
         */

        var unReplayableEnvelope = ObjectMother.Envelope();
        var replayableEnvelope = ObjectMother.Envelope();
        await thePersistence.Inbox.StoreIncomingAsync(unReplayableEnvelope);
        await thePersistence.Inbox.StoreIncomingAsync(replayableEnvelope);

        var divideByZeroException = new DivideByZeroException("Kaboom!");
        var applicationException = new ApplicationException("Kaboom!");
        await thePersistence.Inbox.MoveToDeadLetterStorageAsync(unReplayableEnvelope, divideByZeroException);
        await thePersistence.Inbox.MoveToDeadLetterStorageAsync(replayableEnvelope, applicationException);

        // make one of the messages(DivideByZeroException) replayable
        var replayableErrorMessagesCountAfterMakingReplayable = await thePersistence
            .Admin
            .MarkDeadLetterEnvelopesAsReplayableAsync(divideByZeroException.GetType().FullName!);

        // run the action
        var operation = new MoveReplayableErrorMessagesToIncomingOperation(thePersistence);
        var batch = new DatabaseOperationBatch(thePersistence, new IDatabaseOperation[] { operation });
        await theHost.InvokeAsync(batch);

        var counts = await thePersistence.Admin.FetchCountsAsync();

        replayableErrorMessagesCountAfterMakingReplayable.ShouldBe(1);
        counts.DeadLetter.ShouldBe(1);
        counts.Incoming.ShouldBe(1);
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


        var settings = theHost.Services.GetRequiredService<IMessageStore>().ShouldBeOfType<PostgresqlMessageStore>();

        var counts1 = await settings.LoadPageOfGloballyOwnedIncomingAsync(localOne, 1000);
        var counts2 = await settings.LoadPageOfGloballyOwnedIncomingAsync(localTwo, 1000);

        
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
        for (var i = 0; i < 1000; i++)
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
        var one = await thePersistence.As<IMessageDatabase>().LoadPageOfGloballyOwnedIncomingAsync(localOne, limit);
        foreach (var envelope in one)
        {
            envelope.Destination.ShouldBe(localOne);
            envelope.OwnerId.ShouldBe(TransportConstants.AnyNode);
            envelope.Status.ShouldBe(EnvelopeStatus.Incoming);
        }

        one.Count.ShouldBe(limit);
    }
}