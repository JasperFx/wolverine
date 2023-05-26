using System.Data.Common;
using Microsoft.Extensions.Logging;
using Npgsql;
using NpgsqlTypes;
using Weasel.Core;
using Weasel.Postgresql;
using Weasel.Postgresql.Tables;
using Wolverine.Logging;
using Wolverine.Postgresql.Schema;
using Wolverine.Postgresql.Util;
using Wolverine.RDBMS;
using Wolverine.Runtime.Agents;
using Wolverine.Transports;
using DbCommandBuilder = Weasel.Core.DbCommandBuilder;

namespace Wolverine.Postgresql;

internal class PostgresqlMessageStore : MessageDatabase<NpgsqlConnection>
{
    private readonly string _deleteIncomingEnvelopesSql;
    private readonly string _deleteOutgoingEnvelopesSql;
    private readonly string _discardAndReassignOutgoingSql;
    private readonly string _findAtLargeEnvelopesSql;
    private readonly string _reassignIncomingSql;
    private readonly string _reassignOutgoingSql;


    public PostgresqlMessageStore(DatabaseSettings databaseSettings, DurabilitySettings settings,
        ILogger<PostgresqlMessageStore> logger) : base(databaseSettings,
        settings, logger, new PostgresqlMigrator(), "public")
    {
        _deleteIncomingEnvelopesSql =
            $"delete from {SchemaName}.{DatabaseConstants.IncomingTable} WHERE id = ANY(@ids);";
        _reassignOutgoingSql =
            $"update {SchemaName}.{DatabaseConstants.OutgoingTable} set owner_id = @owner where id = ANY(@ids)";
        _reassignIncomingSql =
            $"update {SchemaName}.{DatabaseConstants.IncomingTable} set owner_id = @owner where id = ANY(@ids)";
        _deleteOutgoingEnvelopesSql =
            $"delete from {SchemaName}.{DatabaseConstants.OutgoingTable} WHERE id = ANY(@ids);";

        _findAtLargeEnvelopesSql =
            $"select {DatabaseConstants.IncomingFields} from {SchemaName}.{DatabaseConstants.IncomingTable} where owner_id = {TransportConstants.AnyNode} and status = '{EnvelopeStatus.Incoming}' and {DatabaseConstants.ReceivedAt} = :address limit :limit";

        _discardAndReassignOutgoingSql = _deleteOutgoingEnvelopesSql +
                                         $";update {SchemaName}.{DatabaseConstants.OutgoingTable} set owner_id = @node where id = ANY(@rids)";
    }

    protected override INodeAgentPersistence? buildNodeStorage(DatabaseSettings databaseSettings)
    {
        return new PostgresqlNodePersistence(databaseSettings);
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


    public override Task MoveToDeadLetterStorageAsync(Envelope envelope, Exception? exception)
    {
        var builder = ToCommandBuilder();
        builder.Append(_deleteIncomingEnvelopesSql);
        var param = (NpgsqlParameter)builder.AddNamedParameter("ids", DBNull.Value);
        param.Value = new[] { envelope.Id };
        param.NpgsqlDbType = NpgsqlDbType.Uuid | NpgsqlDbType.Array;

        DatabasePersistence.ConfigureDeadLetterCommands(envelope, exception, builder, this);

        return builder.Compile().ExecuteOnce(_cancellation);
    }


    public override Task DeleteIncomingEnvelopesAsync(Envelope[] envelopes)
    {
        return CreateCommand(_deleteIncomingEnvelopesSql)
            .WithEnvelopeIds("ids", envelopes)
            .ExecuteOnce(_cancellation);
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

    public override Task ReassignOutgoingAsync(int ownerId, Envelope[] outgoing)
    {
        if (Session.Transaction == null)
        {
            throw new InvalidOperationException("No active transaction");
        }

        return Session.Transaction.CreateCommand(_reassignOutgoingSql)
            .With("owner", ownerId)
            .WithEnvelopeIds("ids", outgoing)
            .ExecuteNonQueryAsync(_cancellation);
    }

    public override Task<IReadOnlyList<Envelope>> LoadPageOfGloballyOwnedIncomingAsync(Uri listenerAddress, int limit)
    {
        return Session
            .CreateCommand(_findAtLargeEnvelopesSql)
            .With("address", listenerAddress.ToString())
            .With("limit", limit)
            .FetchListAsync(r => DatabasePersistence.ReadIncomingAsync(r));
    }

    public override Task ReassignIncomingAsync(int ownerId, IReadOnlyList<Envelope> incoming)
    {
        return Session.CreateCommand(_reassignIncomingSql)
            .With("owner", ownerId)
            .With("ids", incoming.Select(x => x.Id).ToArray())
            .ExecuteNonQueryAsync(_cancellation);
    }

    public override Task<IReadOnlyList<Envelope>> LoadScheduledToExecuteAsync(DateTimeOffset utcNow)
    {
        return Session!.Transaction!
            .CreateCommand(
                $"select {DatabaseConstants.IncomingFields} from {SchemaName}.{DatabaseConstants.IncomingTable} where status = '{EnvelopeStatus.Scheduled}' and execution_time <= @time LIMIT {Durability.RecoveryBatchSize}")
            .With("time", utcNow)
            .FetchListAsync(r => DatabasePersistence.ReadIncomingAsync(r, _cancellation), _cancellation);
    }

    public override void WriteLoadScheduledEnvelopeSql(DbCommandBuilder builder, DateTimeOffset utcNow)
    {
        builder.Append(
            $"select {DatabaseConstants.IncomingFields} from {SchemaName}.{DatabaseConstants.IncomingTable} where status = '{EnvelopeStatus.Scheduled}' and execution_time <= @");
        
        builder.AppendParameter(utcNow);
        builder.Append($" LIMIT {Durability.RecoveryBatchSize};");
    }

    public override Task GetGlobalTxLockAsync(DbConnection conn, DbTransaction tx, int lockId,
        CancellationToken cancellation = default)
    {
        return tx.CreateCommand("SELECT pg_advisory_xact_lock(:id);").With("id", lockId)
            .ExecuteNonQueryAsync(cancellation);
    }

    public override async Task<bool> TryGetGlobalTxLockAsync(DbConnection conn, DbTransaction tx, int lockId,
        CancellationToken cancellation = default)
    {
        var c = await tx.CreateCommand("SELECT pg_try_advisory_xact_lock(:id);")
            .With("id", lockId)
            .ExecuteScalarAsync(cancellation);

        return (bool)c!;
    }

    public override Task GetGlobalLockAsync(DbConnection conn, int lockId, CancellationToken cancellation = default,
        DbTransaction? transaction = null)
    {
        return conn.CreateCommand("SELECT pg_advisory_lock(:id);").With("id", lockId)
            .ExecuteNonQueryAsync(cancellation);
    }

    public override async Task<bool> TryGetGlobalLockAsync(DbConnection conn, DbTransaction? tx, int lockId,
        CancellationToken cancellation = default)
    {
        var c = await conn.CreateCommand("SELECT pg_try_advisory_lock(:id);")
            .With("id", lockId)
            .ExecuteScalarAsync(cancellation);

        return (bool)c!;
    }

    public override async Task<bool> TryGetGlobalLockAsync(DbConnection conn, int lockId, DbTransaction tx,
        CancellationToken cancellation = default)
    {
        var c = await conn.CreateCommand("SELECT pg_try_advisory_xact_lock(:id);")
            .With("id", lockId)
            .ExecuteScalarAsync(cancellation);

        return (bool)c!;
    }

    public override Task ReleaseGlobalLockAsync(DbConnection conn, int lockId,
        CancellationToken cancellation = default,
        DbTransaction? tx = null)
    {
        return conn.CreateCommand("SELECT pg_advisory_unlock(:id);").With("id", lockId)
            .ExecuteNonQueryAsync(cancellation);
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
            assignmentTable.AddColumn<Guid>("node_id").ForeignKeyTo(nodeTable.Identifier, "id", onDelete:CascadeAction.Cascade);
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
        }


    }
}