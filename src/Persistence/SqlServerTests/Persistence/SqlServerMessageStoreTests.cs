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
using Wolverine.Runtime.Agents;
using Wolverine.Runtime.WorkerQueues;
using Wolverine.SqlServer;
using Wolverine.Tracking;
using Wolverine.Transports.Local;

namespace SqlServerTests.Persistence;

public class SqlServerMessageStoreTests : MessageStoreCompliance
{
    public override async Task<IHost> BuildCleanHost()
    {
        var host = await Host.CreateDefaultBuilder()
            .UseWolverine(opts => { opts.PersistMessagesWithSqlServer(Servers.SqlServerConnectionString, "receiver"); })
            .StartAsync();

        var persistence = (IMessageDatabase)host.Services.GetRequiredService<IMessageStore>();
        await persistence.Admin.ClearAllAsync();

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
        await thePersistence
            .DeadLetters
            .MarkDeadLetterEnvelopesAsReplayableAsync(divideByZeroException.GetType().FullName!);

        // run the action
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
    
        [Fact]
    public async Task delete_old_log_node_records()
    {
        var nodeRecord1 = new NodeRecord()
        {
            AgentUri = new Uri("fake://one"), 
            Description = "Started", 
            Id = Guid.NewGuid().ToString(), 
            NodeNumber = 1,
            RecordType = NodeRecordType.AgentStarted, ServiceName = "MyService",
            Timestamp = DateTimeOffset.UtcNow.Subtract(10.Days())
        };
        
        var nodeRecord2 = new NodeRecord()
        {
            AgentUri = new Uri("fake://one"), 
            Description = "Started", 
            Id = Guid.NewGuid().ToString(), 
            NodeNumber = 2,
            RecordType = NodeRecordType.AgentStarted, ServiceName = "MyService",
            Timestamp = DateTimeOffset.UtcNow.Subtract(4.Days())
        };

        var messageDatabase = thePersistence.As<IMessageDatabase>();
        var log = new PersistNodeRecord(messageDatabase.Settings, [nodeRecord1, nodeRecord2]);
        await theHost.InvokeAsync(new DatabaseOperationBatch(messageDatabase, [log]));

        using var conn = new SqlConnection(Servers.SqlServerConnectionString);
        await conn.OpenAsync();
        await conn.CreateCommand(
                $"update receiver.{DatabaseConstants.NodeRecordTableName} set timestamp = @time where node_number = 2")
            .With("time", DateTimeOffset.UtcNow.Subtract(10.Days()))
            .ExecuteNonQueryAsync();
        await conn.CloseAsync();
        
        var recent2 = await thePersistence.Nodes.FetchRecentRecordsAsync(100);
        
        recent2.Any().ShouldBeTrue();

        var op = new DeleteOldNodeEventRecords((IMessageDatabase)thePersistence,
            new DurabilitySettings { NodeEventRecordExpirationTime = 5.Days() });
        
        var batch = new DatabaseOperationBatch((IMessageDatabase)thePersistence, [op]);
        await theHost.InvokeAsync(batch);

        var recent = await thePersistence.Nodes.FetchRecentRecordsAsync(100);
        
        recent.Any().ShouldBeTrue();
        recent.Any(x => x.Id == nodeRecord2.Id).ShouldBeFalse();
    }


}