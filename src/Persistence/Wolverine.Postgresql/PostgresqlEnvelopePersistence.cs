using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Npgsql;
using NpgsqlTypes;
using Weasel.Core;
using Wolverine.Logging;
using Wolverine.RDBMS;
using Wolverine.Postgresql.Util;
using Wolverine.Postgresql.Schema;
using Wolverine.Transports;

namespace Wolverine.Postgresql;

public class PostgresqlEnvelopePersistence : DatabaseBackedEnvelopePersistence<NpgsqlConnection>
{
    private readonly string _deleteIncomingEnvelopesSql;
    private readonly string _deleteOutgoingEnvelopesSql;
    private readonly string _discardAndReassignOutgoingSql;
    private readonly string _findAtLargeEnvelopesSql;
    private readonly string _reassignIncomingSql;
    private readonly string _reassignOutgoingSql;


    public PostgresqlEnvelopePersistence(PostgresqlSettings databaseSettings, AdvancedSettings settings,
        ILogger<PostgresqlEnvelopePersistence> logger) : base(databaseSettings,
        settings, logger)
    {
        _deleteIncomingEnvelopesSql =
            $"delete from {databaseSettings.SchemaName}.{DatabaseConstants.IncomingTable} WHERE id = ANY(@ids);";
        _reassignOutgoingSql =
            $"update {databaseSettings.SchemaName}.{DatabaseConstants.OutgoingTable} set owner_id = @owner where id = ANY(@ids)";
        _reassignIncomingSql =
            $"update {databaseSettings.SchemaName}.{DatabaseConstants.IncomingTable} set owner_id = @owner where id = ANY(@ids)";
        _deleteOutgoingEnvelopesSql =
            $"delete from {databaseSettings.SchemaName}.{DatabaseConstants.OutgoingTable} WHERE id = ANY(@ids);";

        _findAtLargeEnvelopesSql =
            $"select {DatabaseConstants.IncomingFields} from {databaseSettings.SchemaName}.{DatabaseConstants.IncomingTable} where owner_id = {TransportConstants.AnyNode} and status = '{EnvelopeStatus.Incoming}' and {DatabaseConstants.ReceivedAt} = :address limit :limit";

        _discardAndReassignOutgoingSql = _deleteOutgoingEnvelopesSql +
                                         $";update {DatabaseSettings.SchemaName}.{DatabaseConstants.OutgoingTable} set owner_id = @node where id = ANY(@rids)";
    }


    public override ISchemaObject[] Objects
    {
        get
        {
            return new ISchemaObject[]
            {
                new OutgoingEnvelopeTable(DatabaseSettings.SchemaName),
                new IncomingEnvelopeTable(DatabaseSettings.SchemaName),
                new DeadLettersTable(DatabaseSettings.SchemaName)
            };
        }
    }

    protected override bool isExceptionFromDuplicateEnvelope(Exception ex)
    {
        if (ex is PostgresException postgresException)
        {
            return 
                postgresException.Message.Contains("duplicate key value violates unique constraint") ;
        }

        return false;
    }

    public override async Task<PersistedCounts> FetchCountsAsync()
    {
        var counts = new PersistedCounts();

        await using var conn = DatabaseSettings.CreateConnection();
        await conn.OpenAsync();


        await using (var reader = await conn
                         .CreateCommand(
                             $"select status, count(*) from {DatabaseSettings.SchemaName}.{DatabaseConstants.IncomingTable} group by status")
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
            .CreateCommand($"select count(*) from {DatabaseSettings.SchemaName}.{DatabaseConstants.OutgoingTable}")
            .ExecuteScalarAsync();

        counts.Outgoing = Convert.ToInt32(longCount);


        return counts;
    }


    public override Task MoveToDeadLetterStorageAsync(ErrorReport[] errors)
    {
        var builder = DatabaseSettings.ToCommandBuilder();
        builder.Append(_deleteIncomingEnvelopesSql);
        var param = (NpgsqlParameter)builder.AddNamedParameter("ids", DBNull.Value);
        param.Value = errors.Select(x => x.Id).ToArray();
        param.NpgsqlDbType = NpgsqlDbType.Uuid | NpgsqlDbType.Array;

        DatabasePersistence.ConfigureDeadLetterCommands(errors, builder, DatabaseSettings);

        return builder.Compile().ExecuteOnce(_cancellation);
    }


    public override Task DeleteIncomingEnvelopesAsync(Envelope[] envelopes)
    {
        return DatabaseSettings.CreateCommand(_deleteIncomingEnvelopesSql)
            .WithEnvelopeIds("ids", envelopes)
            .ExecuteOnce(_cancellation);
    }


    public override void Describe(TextWriter writer)
    {
        writer.WriteLine($"Persistent Envelope storage using Postgresql in schema '{DatabaseSettings.SchemaName}'");
    }

    public override Task DiscardAndReassignOutgoingAsync(Envelope[] discards, Envelope[] reassigned, int nodeId)
    {
        return DatabaseSettings.CreateCommand(_discardAndReassignOutgoingSql)
            .WithEnvelopeIds("ids", discards)
            .With("node", nodeId)
            .With("rids", reassigned)
            .ExecuteOnce(_cancellation);
    }

    public override Task DeleteOutgoingAsync(Envelope[] envelopes)
    {
        return DatabaseSettings.CreateCommand(_deleteOutgoingEnvelopesSql)
            .WithEnvelopeIds("ids", envelopes)
            .ExecuteOnce(_cancellation);
    }


    protected override string determineOutgoingEnvelopeSql(DatabaseSettings databaseSettings, AdvancedSettings settings)
    {
        return
            $"select {DatabaseConstants.OutgoingFields} from {databaseSettings.SchemaName}.{DatabaseConstants.OutgoingTable} where owner_id = {TransportConstants.AnyNode} and destination = @destination LIMIT {settings.RecoveryBatchSize}";
    }

    public override Task ReassignOutgoingAsync(int ownerId, Envelope[] outgoing)
    {
        if (Session.Transaction == null) throw new InvalidOperationException("No active transaction");
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
            .FetchList(r => DatabasePersistence.ReadIncomingAsync(r));
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
        return Session.Transaction
            .CreateCommand(
                $"select {DatabaseConstants.IncomingFields} from {DatabaseSettings.SchemaName}.{DatabaseConstants.IncomingTable} where status = '{EnvelopeStatus.Scheduled}' and execution_time <= @time LIMIT {Settings.RecoveryBatchSize}")
            .With("time", utcNow)
            .FetchList(r => DatabasePersistence.ReadIncomingAsync(r, _cancellation), _cancellation);
    }
}
