using System.Data;
using System.Data.Common;
using JasperFx.Core;
using JasperFx.Core.Reflection;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Logging;
using Weasel.Core;
using Weasel.SqlServer;
using Weasel.SqlServer.Tables;
using Wolverine.Logging;
using Wolverine.RDBMS;
using Wolverine.Runtime.Agents;
using Wolverine.Runtime.WorkerQueues;
using Wolverine.SqlServer.Schema;
using Wolverine.SqlServer.Util;
using Wolverine.Transports;
using CommandExtensions = Weasel.Core.CommandExtensions;
using DbCommandBuilder = Weasel.Core.DbCommandBuilder;

namespace Wolverine.SqlServer.Persistence;

public class SqlServerMessageStore : MessageDatabase<SqlConnection>
{
    private readonly string _findAtLargeEnvelopesSql;
    private readonly string _moveToDeadLetterStorageSql;
    private readonly string _scheduledLockId;


    public SqlServerMessageStore(DatabaseSettings database, DurabilitySettings settings,
        ILogger<SqlServerMessageStore> logger)
        : base(database, SqlClientFactory.Instance.CreateDataSource(database.ConnectionString), settings, logger, new SqlServerMigrator(), "dbo")
    {
        _findAtLargeEnvelopesSql =
            $"select top (@limit) {DatabaseConstants.IncomingFields} from {database.SchemaName}.{DatabaseConstants.IncomingTable} where owner_id = {TransportConstants.AnyNode} and status = '{EnvelopeStatus.Incoming}' and {DatabaseConstants.ReceivedAt} = @address";

        _moveToDeadLetterStorageSql = $"EXEC {SchemaName}.uspDeleteIncomingEnvelopes @IDLIST;";

        _scheduledLockId = "Wolverine:Scheduled:" + database.ScheduledJobLockId.ToString();
    }

    protected override INodeAgentPersistence? buildNodeStorage(DatabaseSettings databaseSettings,
        DbDataSource dataSource)
    {
        return new SqlServerNodePersistence(databaseSettings, this);
    }

    protected override bool isExceptionFromDuplicateEnvelope(Exception ex)
    {
        return ex is SqlException sqlEx && sqlEx.Message.ContainsIgnoreCase("Violation of PRIMARY KEY constraint");
    }

    public override async Task<PersistedCounts> FetchCountsAsync()
    {
        var counts = new PersistedCounts();

        await using (var reader = await CreateCommand($"select status, count(*) from {SchemaName}.{DatabaseConstants.IncomingTable} group by status")
                         .ExecuteReaderAsync())
        {
            while (await reader.ReadAsync())
            {
                var status = Enum.Parse<EnvelopeStatus>(await reader.GetFieldValueAsync<string>(0));
                var count = await reader.GetFieldValueAsync<int>(1);

                switch (status)
                {
                    case EnvelopeStatus.Incoming:
                        counts.Incoming = count;
                        break;

                    case EnvelopeStatus.Handled:
                        counts.Handled = count;
                        break;

                    case EnvelopeStatus.Scheduled:
                        counts.Scheduled = count;
                        break;
                }
            }

            await reader.CloseAsync();
        }

        counts.Outgoing = (int)(await CreateCommand($"select count(*) from {SchemaName}.{DatabaseConstants.OutgoingTable}")
            .ExecuteScalarAsync())!;

        counts.DeadLetter = (int)(await CreateCommand($"select count(*) from {SchemaName}.{DatabaseConstants.DeadLetterTable}")
            .ExecuteScalarAsync())!;

        return counts;
    }
    
    /// <summary>
    ///     The value of the 'database_principal' parameter in calls to APPLOCK_TEST
    /// </summary>
    public string DatabasePrincipal { get; set; } = "dbo";

    public override async Task MoveToDeadLetterStorageAsync(Envelope envelope, Exception? exception)
    {
        if (HasDisposed) return;
        
        var table = new DataTable();
        table.Columns.Add(new DataColumn("ID", typeof(Guid)));
        table.Rows.Add(envelope.Id);

        var builder = ToCommandBuilder();

        var list = builder.AddNamedParameter("IDLIST", table).As<SqlParameter>();
        list.SqlDbType = SqlDbType.Structured;
        list.TypeName = $"{SchemaName}.EnvelopeIdList";

        builder.Append(_moveToDeadLetterStorageSql);

        DatabasePersistence.ConfigureDeadLetterCommands(envelope, exception, builder, this);

        var cmd = builder.Compile();
        await using var conn = await DataSource.OpenConnectionAsync(_cancellation);
        cmd.Connection = conn;
        
        try
        {
            await cmd.ExecuteNonQueryAsync(_cancellation);
        }
        catch (Exception e)
        {
            if (isExceptionFromDuplicateEnvelope(e)) return;
            throw;
        }
        finally
        {
            await conn.CloseAsync();
        }
    }

    public override void Describe(TextWriter writer)
    {
        writer.WriteLine($"Sql Server Envelope Storage in Schema '{SchemaName}'");
    }

    protected override string determineOutgoingEnvelopeSql(DurabilitySettings settings)
    {
        return
            $"select top {settings.RecoveryBatchSize} {DatabaseConstants.OutgoingFields} from {SchemaName}.{DatabaseConstants.OutgoingTable} where owner_id = {TransportConstants.AnyNode} and destination = @destination";
    }

    public override Task DiscardAndReassignOutgoingAsync(Envelope[] discards, Envelope[] reassigned, int nodeId)
    {
        var cmd = CallFunction("uspDiscardAndReassignOutgoing")
            .WithIdList(this, discards, "discards")
            .WithIdList(this, reassigned, "reassigned")
            .With("ownerId", nodeId);

        return cmd.ExecuteNonQueryAsync(_cancellation);
    }

    public override Task DeleteOutgoingAsync(Envelope[] envelopes)
    {
        if (HasDisposed) return Task.CompletedTask;
        
        return CallFunction("uspDeleteOutgoingEnvelopes")
            .WithIdList(this, envelopes).ExecuteNonQueryAsync(_cancellation);
    }

    public override Task<IReadOnlyList<Envelope>> LoadPageOfGloballyOwnedIncomingAsync(Uri listenerAddress, int limit)
    {
        return CreateCommand(_findAtLargeEnvelopesSql)
            .With("address", listenerAddress.ToString())
            .With("limit", limit)
            .FetchListAsync(r => DatabasePersistence.ReadIncomingAsync(r));
    }

    public override DbCommandBuilder ToCommandBuilder()
    {
        return new DbCommandBuilder(new SqlCommand());
    }

    public override Task ReassignIncomingAsync(int ownerId, IReadOnlyList<Envelope> incoming)
    {
        var cmd = CreateCommand($"{_settings.SchemaName}.uspMarkIncomingOwnership");
        cmd.CommandType = CommandType.StoredProcedure;
        
        return cmd
            .WithIdList(this, incoming)
            .With("owner", ownerId)
            .ExecuteNonQueryAsync(_cancellation);
    }


    public override void WriteLoadScheduledEnvelopeSql(DbCommandBuilder builder, DateTimeOffset utcNow)
    {
        builder.Append( $"select TOP {Durability.RecoveryBatchSize} {DatabaseConstants.IncomingFields} from {SchemaName}.{DatabaseConstants.IncomingTable} where status = '{EnvelopeStatus.Scheduled}' and execution_time <= ");
        builder.AppendParameter(utcNow);
        builder.Append(" order by execution_time");
        builder.Append(';');
    }

    public override async Task PollForScheduledMessagesAsync(ILocalReceiver localQueue,
        ILogger logger,
        DurabilitySettings durabilitySettings, CancellationToken cancellationToken)
    {
        if (HasDisposed) return;
        
        IReadOnlyList<Envelope> envelopes;

        await using var conn = new SqlConnection(_settings.ConnectionString);
        await conn.OpenAsync(cancellationToken);
        try
        {
            var tx = (SqlTransaction)await conn.BeginTransactionAsync(cancellationToken);
            if (await tx.TryGetGlobalTxLock(_scheduledLockId, cancellationToken))
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

                var reassign = conn.CreateCommand($"{_settings.SchemaName}.uspMarkIncomingOwnership", tx);
                reassign.CommandType = CommandType.StoredProcedure;
        
                await reassign
                    .WithIdList(this, envelopes)
                    .With("owner", durabilitySettings.AssignedNodeNumber)
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
        yield return new EnvelopeIdTable(SchemaName);
        yield return new WolverineStoredProcedure("uspDeleteIncomingEnvelopes.sql", this);
        yield return new WolverineStoredProcedure("uspDeleteOutgoingEnvelopes.sql", this);
        yield return new WolverineStoredProcedure("uspDiscardAndReassignOutgoing.sql", this);
        yield return new WolverineStoredProcedure("uspMarkIncomingOwnership.sql", this);
        yield return new WolverineStoredProcedure("uspMarkOutgoingOwnership.sql", this);

        
        if (_settings.IsMaster)
        {
            var nodeTable = new Table(new DbObjectName(SchemaName, DatabaseConstants.NodeTableName));
            nodeTable.AddColumn<Guid>("id").AsPrimaryKey();
            nodeTable.AddColumn<int>("node_number").AutoNumber().NotNull();
            nodeTable.AddColumn<string>("description").NotNull();
            nodeTable.AddColumn<string>("uri").NotNull();
            nodeTable.AddColumn<DateTimeOffset>("started").DefaultValueByExpression("GETUTCDATE()").NotNull();
            nodeTable.AddColumn<DateTimeOffset>("health_check").DefaultValueByExpression("GETUTCDATE()").NotNull();
            nodeTable.AddColumn("capabilities", "nvarchar(max)").AllowNulls();

            yield return nodeTable;

            var assignmentTable = new Table(new DbObjectName(SchemaName, DatabaseConstants.NodeAssignmentsTableName));
            assignmentTable.AddColumn<string>("id").AsPrimaryKey();
            assignmentTable.AddColumn<Guid>("node_id").ForeignKeyTo(nodeTable.Identifier, "id", onDelete:CascadeAction.Cascade);
            assignmentTable.AddColumn<DateTimeOffset>("started").DefaultValueByExpression("GETUTCDATE()").NotNull();

            yield return assignmentTable;
            
            if (_settings.CommandQueuesEnabled)
            {
                var queueTable = new Table(new DbObjectName(SchemaName, DatabaseConstants.ControlQueueTableName));
                queueTable.AddColumn<Guid>("id").AsPrimaryKey();
                queueTable.AddColumn<string>("message_type").NotNull();
                queueTable.AddColumn<Guid>("node_id").NotNull();
                queueTable.AddColumn(DatabaseConstants.Body, "varbinary(max)").NotNull();
                queueTable.AddColumn<DateTimeOffset>("posted").NotNull().DefaultValueByExpression("GETUTCDATE()");
                queueTable.AddColumn<DateTimeOffset>("expires");

                yield return queueTable;
            }
            
                    
            var eventTable = new Table(new DbObjectName(SchemaName, DatabaseConstants.NodeRecordTableName));
            eventTable.AddColumn<int>("id").AutoNumber().AsPrimaryKey();
            eventTable.AddColumn<int>("node_number").NotNull();
            eventTable.AddColumn<string>("event_name").NotNull();
            eventTable.AddColumn<DateTimeOffset>("timestamp").DefaultValueByExpression("GETUTCDATE()").NotNull();
            eventTable.AddColumn("description", "varchar(500)").AllowNulls();
            yield return eventTable;
        }
    }
}