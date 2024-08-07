using IntegrationTests;
using JasperFx.Core;
using Marten;
using Microsoft.Extensions.Hosting;
using Shouldly;
using Weasel.Core;
using Wolverine;
using Wolverine.ComplianceTests;
using Wolverine.Marten;
using Wolverine.RDBMS;
using Wolverine.RDBMS.Durability;
using Wolverine.RDBMS.Polling;
using Wolverine.Transports.Tcp;

namespace PostgresqlTests;

[Collection("marten")]
public class PostgresqlMessageStoreTests : MessageStoreCompliance
{
    public override async Task<IHost> BuildCleanHost()
    {
        var host = await Host.CreateDefaultBuilder()
            .UseWolverine(opts =>
            {
                opts.Services.AddMarten(x =>
                {
                    x.Connection(Servers.PostgresConnectionString);
                    x.DatabaseSchemaName = "receiver";
                }).IntegrateWithWolverine();

                opts.ListenAtPort(2345).UseDurableInbox();
            }).StartAsync();

        var store = host.Get<IDocumentStore>();
        await store.Advanced.Clean.CompletelyRemoveAllAsync();

        return host;
    }

    [Fact]
    public async Task delete_expired_envelopes()
    {
        var envelope = ObjectMother.Envelope();

        await thePersistence.Inbox.StoreIncomingAsync(envelope);

        await thePersistence.Inbox.MarkIncomingEnvelopeAsHandledAsync(envelope);

        var hourAgo = DateTimeOffset.UtcNow.Add(1.Hours());
        var operation =
            new DeleteExpiredEnvelopesOperation(new DbObjectName("receiver", DatabaseConstants.IncomingTable), hourAgo);
        var batch = new DatabaseOperationBatch((IMessageDatabase)thePersistence, [operation]);
        await theHost.InvokeAsync(batch);

        var counts = await thePersistence.Admin.FetchCountsAsync();

        counts.Incoming.ShouldBe(0);
        counts.Scheduled.ShouldBe(0);
        counts.Handled.ShouldBe(0);

        // Now, let's set the keep until in the past
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
            .DeadLetters
            .MarkDeadLetterEnvelopesAsReplayableAsync(divideByZeroException.GetType().FullName!);

        // run the action
        var operation = new MoveReplayableErrorMessagesToIncomingOperation((IMessageDatabase)thePersistence);
        var batch = new DatabaseOperationBatch((IMessageDatabase)thePersistence, [operation]);
        await theHost.InvokeAsync(batch);

        var counts = await thePersistence.Admin.FetchCountsAsync();

        replayableErrorMessagesCountAfterMakingReplayable.ShouldBe(1);
        counts.DeadLetter.ShouldBe(1);
        counts.Incoming.ShouldBe(1);
        counts.Scheduled.ShouldBe(0);
        counts.Handled.ShouldBe(0);
    }

    [Fact]
    public async Task move_dead_letter_messages_as_replayable_by_id_to_incoming()
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
            .MarkDeadLetterEnvelopesAsReplayableAsync([replayableEnvelope.Id]);

        var operation = new MoveReplayableErrorMessagesToIncomingOperation((IMessageDatabase)thePersistence);
        var batch = new DatabaseOperationBatch((IMessageDatabase)thePersistence, [operation]);
        await theHost.InvokeAsync(batch);

        var counts = await thePersistence.Admin.FetchCountsAsync();

        counts.DeadLetter.ShouldBe(1);
        counts.Incoming.ShouldBe(1);
        counts.Scheduled.ShouldBe(0);
        counts.Handled.ShouldBe(0);
    }
}