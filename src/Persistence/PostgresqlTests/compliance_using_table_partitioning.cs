using IntegrationTests;
using JasperFx.Core;
using JasperFx.Core.Reflection;
using JasperFx.Resources;
using Marten;
using Microsoft.Extensions.Hosting;
using Npgsql;
using Shouldly;
using Weasel.Core;
using Wolverine;
using Wolverine.ComplianceTests;
using Wolverine.Marten;
using Wolverine.Persistence.Durability;
using Wolverine.RDBMS;
using Wolverine.RDBMS.Durability;
using Wolverine.RDBMS.Polling;
using Wolverine.Runtime.Agents;
using Wolverine.Transports.Tcp;

namespace PostgresqlTests;

public class compliance_using_table_partitioning : MessageStoreCompliance
{
    public override async Task<IHost> BuildCleanHost()
    {
        #region sample_enabling_inbox_partitioning

        var host = await Host.CreateDefaultBuilder()
            .UseWolverine(opts =>
            {
                opts.Durability.EnableInboxPartitioning = true;
                

                #endregion
                opts.Services.AddMarten(x =>
                {
                    x.Connection(Servers.PostgresConnectionString);
                    x.DatabaseSchemaName = "receiver_partitioned";
                }).IntegrateWithWolverine();

                opts.ListenAtPort(2345).UseDurableInbox();

                opts.Services.AddResourceSetupOnStartup();
            }).StartAsync();

        var store = host.Get<IDocumentStore>();
        await store.Advanced.Clean.CompletelyRemoveAllAsync();

        await host.ResetResourceState();

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
            new DeleteExpiredEnvelopesOperation(new DbObjectName("receiver_partitioned", DatabaseConstants.IncomingTable), hourAgo);
        var batch = new DatabaseOperationBatch((IMessageDatabase)thePersistence, [operation]);
        await theHost.InvokeAsync(batch);

        var counts = await thePersistence.Admin.FetchCountsAsync();

        counts.Incoming.ShouldBe(0);
        counts.Scheduled.ShouldBe(0);
        counts.Handled.ShouldBe(0);

        // Now, let's set the keep until in the past
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

        using var conn = new NpgsqlConnection(Servers.PostgresConnectionString);
        await conn.OpenAsync();
        await conn.CreateCommand(
                $"update receiver.{DatabaseConstants.NodeRecordTableName} set timestamp = :time where node_number = 2")
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
}