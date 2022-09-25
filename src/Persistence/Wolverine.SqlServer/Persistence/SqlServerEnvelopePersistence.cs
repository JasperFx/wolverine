using System;
using System.Collections.Generic;
using System.Data;
using System.IO;
using System.Threading.Tasks;
using Baseline;
using Wolverine.Logging;
using Wolverine.RDBMS;
using Wolverine.SqlServer.Util;
using Wolverine.Transports;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Logging;
using Weasel.Core;
using Wolverine.SqlServer.Schema;

namespace Wolverine.SqlServer.Persistence;

public class SqlServerEnvelopePersistence : DatabaseBackedEnvelopePersistence<SqlConnection>
{
    private readonly SqlServerSettings _databaseSettings;
    private readonly string _findAtLargeEnvelopesSql;
    private readonly string _moveToDeadLetterStorageSql;


    public SqlServerEnvelopePersistence(SqlServerSettings databaseSettings, AdvancedSettings settings,
        ILogger<SqlServerEnvelopePersistence> logger)
        : base(databaseSettings, settings, logger)
    {
        _databaseSettings = databaseSettings;
        _findAtLargeEnvelopesSql =
            $"select top {settings.RecoveryBatchSize} {DatabaseConstants.IncomingFields} from {databaseSettings.SchemaName}.{DatabaseConstants.IncomingTable} where owner_id = {TransportConstants.AnyNode} and status = '{EnvelopeStatus.Incoming}'";

        _moveToDeadLetterStorageSql = $"EXEC {_databaseSettings.SchemaName}.uspDeleteIncomingEnvelopes @IDLIST;";
    }

    public override ISchemaObject[] Objects
    {
        get
        {
            return new ISchemaObject[]
            {
                new OutgoingEnvelopeTable(DatabaseSettings.SchemaName),
                new IncomingEnvelopeTable(DatabaseSettings.SchemaName),
                new DeadLettersTable(DatabaseSettings.SchemaName),
                new EnvelopeIdTable(DatabaseSettings.SchemaName),
                new WolverineStoredProcedure("uspDeleteIncomingEnvelopes.sql", DatabaseSettings),
                new WolverineStoredProcedure("uspDeleteOutgoingEnvelopes.sql", DatabaseSettings),
                new WolverineStoredProcedure("uspDiscardAndReassignOutgoing.sql", DatabaseSettings),
                new WolverineStoredProcedure("uspMarkIncomingOwnership.sql", DatabaseSettings),
                new WolverineStoredProcedure("uspMarkOutgoingOwnership.sql", DatabaseSettings)
            };
        }
    }

    protected override bool isExceptionFromDuplicateEnvelope(Exception ex)
    {
        return (ex is SqlException sqlEx && sqlEx.Message.ContainsIgnoreCase("Violation of PRIMARY KEY constraint"));
    }

    public override async Task<PersistedCounts> FetchCountsAsync()
    {
        var counts = new PersistedCounts();

        await using var conn = DatabaseSettings.CreateConnection();
        await conn.OpenAsync();


        await using var reader = await conn
            .CreateCommand(
                $"select status, count(*) from {DatabaseSettings.SchemaName}.{DatabaseConstants.IncomingTable} group by status")
            .ExecuteReaderAsync();

        while (await reader.ReadAsync())
        {
            var status = Enum.Parse<EnvelopeStatus>(await reader.GetFieldValueAsync<string>(0));
            var count = await reader.GetFieldValueAsync<int>(1);

            switch (status)
            {
                case EnvelopeStatus.Incoming:
                    counts.Incoming = count;
                    break;
                case EnvelopeStatus.Scheduled:
                    counts.Scheduled = count;
                    break;
            }
        }

        counts.Outgoing = (int)await conn
            .CreateCommand($"select count(*) from {DatabaseSettings.SchemaName}.{DatabaseConstants.OutgoingTable}")
            .ExecuteScalarAsync();


        return counts;
    }

    public override Task DeleteIncomingEnvelopesAsync(Envelope[] envelopes)
    {
        return _databaseSettings.CallFunction("uspDeleteIncomingEnvelopes")
            .WithIdList(_databaseSettings, envelopes).ExecuteOnce(_cancellation);
    }

    public override Task MoveToDeadLetterStorageAsync(ErrorReport[] errors)
    {
        var table = new DataTable();
        table.Columns.Add(new DataColumn("ID", typeof(Guid)));
        foreach (var error in errors) table.Rows.Add(error.Id);

        var builder = DatabaseSettings.ToCommandBuilder();

        var list = builder.AddNamedParameter("IDLIST", table).As<SqlParameter>();
        list.SqlDbType = SqlDbType.Structured;
        list.TypeName = $"{_databaseSettings.SchemaName}.EnvelopeIdList";

        builder.Append(_moveToDeadLetterStorageSql);

        DatabasePersistence.ConfigureDeadLetterCommands(errors, builder, DatabaseSettings);

        return builder.Compile().ExecuteOnce(_cancellation);
    }

    public override void Describe(TextWriter writer)
    {
        writer.WriteLine($"Sql Server Envelope Storage in Schema '{_databaseSettings.SchemaName}'");
    }

    protected override string determineOutgoingEnvelopeSql(DatabaseSettings databaseSettings,
        AdvancedSettings settings)
    {
        return
            $"select top {settings.RecoveryBatchSize} {DatabaseConstants.OutgoingFields} from {databaseSettings.SchemaName}.{DatabaseConstants.OutgoingTable} where owner_id = {TransportConstants.AnyNode} and destination = @destination";
    }

    public override Task ReassignOutgoingAsync(int ownerId, Envelope[] outgoing)
    {
        var cmd = Session.CallFunction("uspMarkOutgoingOwnership")
            .WithIdList(DatabaseSettings, outgoing)
            .With("owner", ownerId);

        return cmd.ExecuteNonQueryAsync(_cancellation);
    }

    public override Task DiscardAndReassignOutgoingAsync(Envelope[] discards, Envelope[] reassigned, int nodeId)
    {
        var cmd = DatabaseSettings.CallFunction("uspDiscardAndReassignOutgoing")
            .WithIdList(DatabaseSettings, discards, "discards")
            .WithIdList(DatabaseSettings, reassigned, "reassigned")
            .With("ownerId", nodeId);

        return cmd.ExecuteOnce(_cancellation);
    }

    public override Task DeleteOutgoingAsync(Envelope[] envelopes)
    {
        return DatabaseSettings.CallFunction("uspDeleteOutgoingEnvelopes")
            .WithIdList(DatabaseSettings, envelopes).ExecuteOnce(_cancellation);
    }

    public override Task<IReadOnlyList<Envelope>> LoadPageOfGloballyOwnedIncomingAsync()
    {
        return Session.CreateCommand(_findAtLargeEnvelopesSql)
            .FetchList(r => DatabasePersistence.ReadIncomingAsync(r));
    }

    public override Task ReassignIncomingAsync(int ownerId, IReadOnlyList<Envelope> incoming)
    {
        return Session.CallFunction("uspMarkIncomingOwnership")
            .WithIdList(_databaseSettings, incoming)
            .With("owner", ownerId)
            .ExecuteNonQueryAsync(_cancellation);
    }

    public override Task<IReadOnlyList<Envelope>> LoadScheduledToExecuteAsync(DateTimeOffset utcNow)
    {
        return Session.Transaction
            .CreateCommand(
                $"select TOP {Settings.RecoveryBatchSize} {DatabaseConstants.IncomingFields} from {DatabaseSettings.SchemaName}.{DatabaseConstants.IncomingTable} where status = '{EnvelopeStatus.Scheduled}' and execution_time <= @time")
            .With("time", utcNow)
            .FetchList(r => DatabasePersistence.ReadIncomingAsync(r, _cancellation), _cancellation);
    }
}
