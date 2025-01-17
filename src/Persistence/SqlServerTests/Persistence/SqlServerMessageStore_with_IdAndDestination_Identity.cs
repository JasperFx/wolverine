using IntegrationTests;
using JasperFx.Core;
using JasperFx.Core.Reflection;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using Shouldly;
using Weasel.Core;
using Wolverine;
using Wolverine.ComplianceTests;
using Wolverine.Persistence.Durability;
using Wolverine.RDBMS;
using Wolverine.RDBMS.Durability;
using Wolverine.RDBMS.Polling;
using Wolverine.Runtime.WorkerQueues;
using Wolverine.SqlServer;
using Wolverine.SqlServer.Schema;
using Wolverine.Tracking;
using Wolverine.Transports.Local;

namespace SqlServerTests.Persistence;

public class SqlServerMessageStore_with_IdAndDestination_Identity : MessageStoreCompliance
{
    public override async Task<IHost> BuildCleanHost()
    {
        #region sample_configuring_message_identity_to_use_id_and_destination

        var host = await Host.CreateDefaultBuilder()
            .UseWolverine(opts =>
            {
                opts.PersistMessagesWithSqlServer(Servers.SqlServerConnectionString, "receiver2");
                
                // This setting changes the internal message storage identity
                opts.Durability.MessageIdentity = MessageIdentity.IdAndDestination;
            })
            .StartAsync();

        #endregion

        var persistence = (IMessageDatabase)host.Services.GetRequiredService<IMessageStore>();
        await persistence.Admin.ClearAllAsync();

        return host;
    }
    
    [Fact]
    public async Task should_have_receive_at_in_primary_keys()
    {
        using var conn = new SqlConnection(Servers.SqlServerConnectionString);
        await conn.OpenAsync();

        var runtime = theHost.GetRuntime();

        var incoming = await new IncomingEnvelopeTable(runtime.Options.Durability, "receiver2").FetchExistingAsync(conn);
        incoming.PrimaryKeyColumns.ShouldContain(DatabaseConstants.Id);
        incoming.PrimaryKeyColumns.ShouldContain(DatabaseConstants.ReceivedAt);
        
        var dlq = await new DeadLettersTable(runtime.Options.Durability, "receiver2").FetchExistingAsync(conn);
        dlq.PrimaryKeyColumns.ShouldContain(DatabaseConstants.Id);
        dlq.PrimaryKeyColumns.ShouldContain(DatabaseConstants.ReceivedAt);
    }

    [Fact]
    public async Task delete_expired_envelopes()
    {
        var envelope = ObjectMother.Envelope();

        await thePersistence.Inbox.StoreIncomingAsync(envelope);

        await thePersistence.Inbox.MarkIncomingEnvelopeAsHandledAsync(envelope);

        var hourAgo = DateTimeOffset.UtcNow.Add(1.Hours());
        var operation =
            new DeleteExpiredEnvelopesOperation(new DbObjectName("receiver2", DatabaseConstants.IncomingTable), hourAgo);
        var batch = new DatabaseOperationBatch((IMessageDatabase)thePersistence, [operation]);
        await theHost.InvokeAsync(batch);

        var counts = await thePersistence.Admin.FetchCountsAsync();

        counts.Incoming.ShouldBe(0);
        counts.Scheduled.ShouldBe(0);
        counts.Handled.ShouldBe(0);
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

    [Fact]
    public async Task should_reasign_incoming_envelope_to_owner_id()
    {
        var envelope = ObjectMother.Envelope();
        envelope.Status = EnvelopeStatus.Incoming;
        envelope.ScheduledTime = DateTimeOffset.Now;

        await thePersistence.Inbox.StoreIncomingAsync(envelope);
        await thePersistence.Inbox.ScheduleExecutionAsync(envelope);

        var durabilitySettings = theHost.Services.GetRequiredService<DurabilitySettings>();

        var runtime = theHost.GetRuntime();
        var theReceiver = new DurableReceiver(new LocalQueue("temp"), runtime, runtime.Pipeline);
        
        await thePersistence.As<IMessageDatabase>().PollForScheduledMessagesAsync(theReceiver,
            NullLogger.Instance,
            durabilitySettings,
            default);

        var stored = (await thePersistence.Admin.AllIncomingAsync()).Single();

        stored.OwnerId.ShouldBe(durabilitySettings.AssignedNodeNumber);
        stored.Status.ShouldBe(EnvelopeStatus.Incoming);
    }

}