using Microsoft.Extensions.Logging;
using Npgsql;
using NpgsqlTypes;
using Weasel.Core;
using Weasel.Core.Migrations;
using Weasel.Postgresql;
using Weasel.Postgresql.Tables;
using Wolverine.Logging;
using Wolverine.Postgresql.Schema;
using Wolverine.Postgresql.Util;
using Wolverine.RDBMS;
using Wolverine.Runtime.Agents;
using Wolverine.Runtime.WorkerQueues;
using Wolverine.Transports;

namespace Wolverine.Postgresql;

internal class PostgresqlMessageStore : MessageDatabase<NpgsqlConnection>
{
    private readonly string _deleteIncomingEnvelopesSql;
    private readonly string _deleteOutgoingEnvelopesSql;
    private readonly string _discardAndReassignOutgoingSql;
    private readonly string _findAtLargeEnvelopesSql;
    private readonly string _reassignIncomingSql;


    public PostgresqlMessageStore(DatabaseSettings databaseSettings, DurabilitySettings settings,
        ILogger<PostgresqlMessageStore> logger) : base(databaseSettings,
        settings, logger, new PostgresqlMigrator(), "public")
    {
        _deleteIncomingEnvelopesSql =
            $"delete from {SchemaName}.{DatabaseConstants.IncomingTable} WHERE id = ANY(@ids);";

        _reassignIncomingSql =
            $"update {SchemaName}.{DatabaseConstants.IncomingTable} set owner_id = @owner where id = ANY(@ids)";
        _deleteOutgoingEnvelopesSql =
            $"delete from {SchemaName}.{DatabaseConstants.OutgoingTable} WHERE id = ANY(@ids);";

        _findAtLargeEnvelopesSql =
            $"select {DatabaseConstants.IncomingFields} from {SchemaName}.{DatabaseConstants.IncomingTable} where owner_id = {TransportConstants.AnyNode} and status = '{EnvelopeStatus.Incoming}' and {DatabaseConstants.ReceivedAt} = :address limit :limit";

        _discardAndReassignOutgoingSql = _deleteOutgoingEnvelopesSql +
                                         $";update {SchemaName}.{DatabaseConstants.OutgoingTable} set owner_id = @node where id = ANY(@rids)";
    }

    protected override INodeAgentPersistence buildNodeStorage(DatabaseSettings databaseSettings)
    {
        return new PostgresqlNodePersistence(databaseSettings, this);
    }

    protected override bool isExceptionFromDuplicateEnvelope(Exception ex)
    {
        if (ex is PostgresException postgresException)
        {
            return
                postgresException.Message.Contains("duplicate key value violates unique constraint");
        }

        return false;
    }

    public override async Task<PersistedCounts> FetchCountsAsync()
    {
        var counts = new PersistedCounts();

        await using var conn = CreateConnection();
        await conn.OpenAsync();


        await using (var reader = await conn
                         .CreateCommand(
                             $"select status, count(*) from {SchemaName}.{DatabaseConstants.IncomingTable} group by status")
                         .ExecuteReaderAsync())
        {
            while (await reader.ReadAsync())
            {
                var status = Enum.Parse<EnvelopeStatus>(await reader.GetFieldValueAsync<string>(0));
                var count = await reader.GetFieldValueAsync<int>(1);

                if (status == EnvelopeStatus.Incoming)
                {
                    counts.Incoming = count;
                }
                else if (status == EnvelopeStatus.Handled)
                {
                    counts.Handled = count;
                }
                else if (status == EnvelopeStatus.Scheduled)
                {
                    counts.Scheduled = count;
                }
            }
        }

        var longCount = await conn
            .CreateCommand($"select count(*) from {SchemaName}.{DatabaseConstants.OutgoingTable}")
            .ExecuteScalarAsync();

        counts.Outgoing = Convert.ToInt32(longCount);

        var deadLetterCount = await conn
            .CreateCommand($"select count(*) from {SchemaName}.{DatabaseConstants.DeadLetterTable}")
            .ExecuteScalarAsync();

        counts.DeadLetter = Convert.ToInt32(deadLetterCount);

        await conn.CloseAsync();

        return counts;
    }


    public override async Task MoveToDeadLetterStorageAsync(Envelope envelope, Exception? exception)
    {
        try
        {
            var builder = ToCommandBuilder();
            builder.Append(_deleteIncomingEnvelopesSql);
            var param = (NpgsqlParameter)builder.AddNamedParameter("ids", DBNull.Value);
            param.Value = new[] { envelope.Id };
            // ReSharper disable once BitwiseOperatorOnEnumWithoutFlags
            param.NpgsqlDbType = NpgsqlDbType.Uuid | NpgsqlDbType.Array;

            DatabasePersistence.ConfigureDeadLetterCommands(envelope, exception, builder, this);

            await builder.Compile().ExecuteOnce(_cancellation);
        }
        catch (Exception e)
        {
            if (isExceptionFromDuplicateEnvelope(e)) return;
            throw;
        }
    }


    public override void Describe(TextWriter writer)
    {
        writer.WriteLine($"Persistent Envelope storage using Postgresql in schema '{SchemaName}'");
    }

    public override Task DiscardAndReassignOutgoingAsync(Envelope[] discards, Envelope[] reassigned, int nodeId)
    {
        return CreateCommand(_discardAndReassignOutgoingSql)
            .WithEnvelopeIds("ids", discards)
            .With("node", nodeId)
            .WithEnvelopeIds("rids", reassigned)
            .ExecuteOnce(_cancellation);
    }

    public override Task DeleteOutgoingAsync(Envelope[] envelopes)
    {
        return CreateCommand(_deleteOutgoingEnvelopesSql)
            .WithEnvelopeIds("ids", envelopes)
            .ExecuteOnce(_cancellation);
    }


    protected override string determineOutgoingEnvelopeSql(DurabilitySettings settings)
    {
        return
            $"select {DatabaseConstants.OutgoingFields} from {SchemaName}.{DatabaseConstants.OutgoingTable} where owner_id = {TransportConstants.AnyNode} and destination = @destination LIMIT {settings.RecoveryBatchSize}";
    }

    public override async Task<IReadOnlyList<Envelope>> LoadPageOfGloballyOwnedIncomingAsync(Uri listenerAddress,
        int limit)
    {
        await using var conn = CreateConnection();
        await conn.OpenAsync();

        var list = await conn
            .CreateCommand(_findAtLargeEnvelopesSql)
            .With("address", listenerAddress.ToString())
            .With("limit", limit)
            .FetchListAsync(r => DatabasePersistence.ReadIncomingAsync(r));

        await conn.CloseAsync();

        return list;
    }

    public override async Task ReassignIncomingAsync(int ownerId, IReadOnlyList<Envelope> incoming)
    {
        await using var conn = CreateConnection();
        await conn.OpenAsync();
        await conn.CreateCommand(_reassignIncomingSql)
            .With("owner", ownerId)
            .With("ids", incoming.Select(x => x.Id).ToArray())
            .ExecuteNonQueryAsync(_cancellation);

        await conn.CloseAsync();
    }

    public override void WriteLoadScheduledEnvelopeSql(DbCommandBuilder builder, DateTimeOffset utcNow)
    {
        builder.Append(
            $"select {DatabaseConstants.IncomingFields} from {SchemaName}.{DatabaseConstants.IncomingTable} where status = '{EnvelopeStatus.Scheduled}' and execution_time <= ");

        builder.AppendParameter(utcNow);
        builder.Append($" order by execution_time LIMIT {Durability.RecoveryBatchSize};");
    }

    public override async Task PollForScheduledMessagesAsync(ILocalReceiver localQueue, ILogger logger,
        DurabilitySettings durabilitySettings, CancellationToken cancellationToken)
    {
        IReadOnlyList<Envelope> envelopes;
        
        using var conn = new NpgsqlConnection(_settings.ConnectionString);
        await conn.OpenAsync(cancellationToken);
        try
        {
            var tx = await conn.BeginTransactionAsync(cancellationToken);
            if (await tx.TryGetGlobalTxLock(Settings.ScheduledJobLockId, cancellationToken) == AttainLockResult.Success)
            {
                var builder = new DbCommandBuilder(conn);
                WriteLoadScheduledEnvelopeSql(builder, DateTimeOffset.UtcNow);
                var cmd = builder.Compile();
                cmd.Connection = conn;
                cmd.Transaction = tx;

                envelopes = await cmd.FetchListAsync(reader =>
                    DatabasePersistence.ReadIncomingAsync(reader, cancellationToken), cancellation: cancellationToken);

                if (!envelopes.Any())
                {
                    await tx.RollbackAsync(cancellationToken);
                    return;
                }

                await conn.CreateCommand(_reassignIncomingSql)
                    .With("owner", durabilitySettings.AssignedNodeNumber)
                    .With("ids", envelopes.Select(x => x.Id).ToArray())
                    .ExecuteNonQueryAsync(_cancellation);


                await tx.CommitAsync(cancellationToken);
                
                // Judging that there's very little chance of errors here
                foreach (var envelope in envelopes)
                {
                    logger.LogInformation("Locally enqueuing scheduled message {Id} of type {MessageType}", envelope.Id,
                        envelope.MessageType);
                    localQueue.Enqueue(envelope);
                }
            }
        }
        finally
        {
            await conn.CloseAsync();
        }
    }

    public override IEnumerable<ISchemaObject> AllObjects()
    {
        yield return new OutgoingEnvelopeTable(SchemaName);
        yield return new IncomingEnvelopeTable(SchemaName);
        yield return new DeadLettersTable(SchemaName);
        
        


        if (_settings.IsMaster)
        {
            var nodeTable = new Table(new DbObjectName(SchemaName, DatabaseConstants.NodeTableName));
            nodeTable.AddColumn<Guid>("id").AsPrimaryKey();
            nodeTable.AddColumn("node_number", "SERIAL").NotNull();
            nodeTable.AddColumn<string>("description").NotNull();
            nodeTable.AddColumn<string>("uri").NotNull();
            nodeTable.AddColumn<DateTimeOffset>("started").DefaultValueByExpression("now()").NotNull();
            nodeTable.AddColumn<DateTimeOffset>("health_check").NotNull().DefaultValueByExpression("now()");
            nodeTable.AddColumn("capabilities", "text[]").AllowNulls();

            yield return nodeTable;

            var assignmentTable = new Table(new DbObjectName(SchemaName, DatabaseConstants.NodeAssignmentsTableName));
            assignmentTable.AddColumn<string>("id").AsPrimaryKey();
            assignmentTable.AddColumn<Guid>("node_id")
                .ForeignKeyTo(nodeTable.Identifier, "id", onDelete: CascadeAction.Cascade);
            assignmentTable.AddColumn<DateTimeOffset>("started").DefaultValueByExpression("now()").NotNull();

            yield return assignmentTable;

            if (_settings.CommandQueuesEnabled)
            {
                var queueTable = new Table(new DbObjectName(SchemaName, DatabaseConstants.ControlQueueTableName));
                queueTable.AddColumn<Guid>("id").AsPrimaryKey();
                queueTable.AddColumn<string>("message_type").NotNull();
                queueTable.AddColumn<Guid>("node_id").NotNull();
                queueTable.AddColumn(DatabaseConstants.Body, "bytea").NotNull();
                queueTable.AddColumn<DateTimeOffset>("posted").NotNull().DefaultValueByExpression("NOW()");
                queueTable.AddColumn<DateTimeOffset>("expires");

                yield return queueTable;
            }
            
            var eventTable = new Table(new DbObjectName(SchemaName, DatabaseConstants.NodeRecordTableName));
            eventTable.AddColumn("id", "SERIAL").AsPrimaryKey();
            eventTable.AddColumn<int>("node_number").NotNull();
            eventTable.AddColumn<string>("event_name").NotNull();
            eventTable.AddColumn<DateTimeOffset>("timestamp").DefaultValueByExpression("now()").NotNull();
            eventTable.AddColumn<string>("description").AllowNulls();
            yield return eventTable;
        }
    }
}