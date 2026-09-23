using System.Data.Common;
using JasperFx;
using JasperFx.Core;
using Oracle.ManagedDataAccess.Client;
using Weasel.Core;
using Weasel.Oracle;
using Wolverine.Oracle.Util;
using Wolverine.RDBMS.Transport;
using Wolverine.Runtime;
using Wolverine.Transports;
using Table = Weasel.Oracle.Tables.Table;

namespace Wolverine.Oracle;

internal partial class OracleMessageStore
{
    public async Task PollForMessagesFromExternalTablesAsync(IListener listener, IWolverineRuntime settings,
        ExternalMessageTable externalTable, IReceiver receiver, CancellationToken token)
    {
        // Mirrors MessageDatabase<T>.PollForMessagesFromExternalTablesAsync. The
        // advisory lock serializes pollers across nodes; unlike the shared base
        // (where closing the poll connection releases the lock), OracleAdvisoryLock
        // holds its own dedicated connection, so the lock must be released explicitly
        // after each poll or other nodes could never take over.
        if (!await AdvisoryLock.TryAttainLockAsync(externalTable.AdvisoryLock, token))
        {
            return;
        }

        try
        {
            await using var conn = CreateConnection();
            await conn.OpenAsync(token);

            await using var command = buildFetchSql(conn, externalTable.TableName, externalTable.Columns().ToArray(),
                externalTable.MessageBatchSize);

            var envelopes = (await command.FetchListAsync(
                r => OracleEnvelopeReader.ReadExternalAsync(r, externalTable), token)).ToArray();

            if (envelopes.Any())
            {
                // Important to mark every envelope as being owned by the
                // current node
                foreach (var envelope in envelopes)
                {
                    envelope.Status = EnvelopeStatus.Incoming;
                    envelope.OwnerId = settings.DurabilitySettings.AssignedNodeNumber;

                    // Fix for GH-1225
                    envelope.Source = externalTable.TableName.QualifiedName;
                }

                var tx = await conn.BeginTransactionAsync(token);
                await StoreIncomingAsync(tx, envelopes);

                await deleteManyAsync(tx, envelopes.Select(x => x.Id).ToArray(),
                    externalTable.TableName, externalTable.IdColumnName, token);
                await tx.CommitAsync(token);

                await receiver.ReceivedAsync(listener, envelopes);
            }

            await conn.CloseAsync();
        }
        finally
        {
            // Deliberately no catch: ExternalMessageTableListener already wraps this call and logs
            // the failure with its ILogger and the table name, then keeps polling. Catching here
            // pre-empted that -- an Oracle poll failure produced a bare message on stderr, no stack
            // trace, and nothing in the configured log sink. MessageDatabase<T>'s implementation
            // does not catch either. ReleaseLockAsync swallows and logs its own errors, so this
            // finally cannot mask the exception on its way out.
            await AdvisoryLock.ReleaseLockAsync(externalTable.AdvisoryLock);
        }
    }

    private OracleCommand buildFetchSql(OracleConnection conn, DbObjectName tableName, string[] columnNames, int maxRecords)
    {
        return conn.CreateCommand($"SELECT {columnNames.Join(", ")} FROM {tableName.QualifiedName} FETCH FIRST {maxRecords} ROWS ONLY");
    }

    private static async Task deleteManyAsync(DbTransaction tx, Guid[] ids, DbObjectName tableName, string mapperIdColumnName, CancellationToken token)
    {
        // Oracle has no = ANY(array); bind an explicit IN list. Batch sizes are
        // bounded by ExternalMessageTable.MessageBatchSize (default 100), well
        // under Oracle's 1000-item IN list limit.
        using var cmd = tx.CreateCommand("");
        var placeholders = OracleCommandExtensions.WithIdList(cmd, "id", ids);
        cmd.CommandText = $"DELETE FROM {tableName.QualifiedName} WHERE {mapperIdColumnName} IN ({placeholders})";
        await cmd.ExecuteNonQueryAsync(token);
    }

    public async Task MigrateExternalMessageTable(ExternalMessageTable definition)
    {
        var table = AddExternalMessageTable(definition);
        await using var conn = CreateConnection();
        await conn.OpenAsync();

        // The Oracle builder, not the default one: ODP.NET will not execute several statements from
        // one command, and Oracle's Table registers six introspection queries as of Weasel 9.25
        // (weasel#474). DbCommandBuilder.StartNewCommand is a no-op, so the default builder runs
        // them together and Oracle rejects the batch with ORA-03048.
        var builder = new OracleMigrator().CreateCommandBuilder(conn);
        var migration = await SchemaMigration.DetermineAsync(conn, builder, CancellationToken.None, table);
        if (migration.Difference != SchemaPatchDifference.None)
        {
            await new OracleMigrator().ApplyAllAsync(conn, migration, AutoCreate.CreateOrUpdate);
        }

        await conn.CloseAsync();
    }

    public async Task PublishMessageToExternalTableAsync(ExternalMessageTable table, string? messageTypeName,
        byte[] json, CancellationToken token)
    {
        await using var conn = CreateConnection();
        await conn.OpenAsync(token);

        await using var cmd = conn.CreateCommand("");
        if (table.MessageTypeColumnName.IsEmpty())
        {
            cmd.CommandText =
                $"INSERT INTO {table.TableName.QualifiedName} ({table.IdColumnName}, {table.JsonBodyColumnName}) VALUES (:id, :json)";
            cmd.With("id", Guid.NewGuid());
            cmd.Parameters.Add(new OracleParameter("json", OracleDbType.Blob) { Value = json });
        }
        else
        {
            cmd.CommandText =
                $"INSERT INTO {table.TableName.QualifiedName} ({table.IdColumnName}, {table.JsonBodyColumnName}, {table.MessageTypeColumnName}) VALUES (:id, :json, :message)";
            cmd.With("id", Guid.NewGuid());
            cmd.Parameters.Add(new OracleParameter("json", OracleDbType.Blob) { Value = json });
            cmd.With("message", messageTypeName!);
        }

        await cmd.ExecuteNonQueryAsync(token);
        await conn.CloseAsync();
    }

    public ITable AddExternalMessageTable(ExternalMessageTable definition)
    {
        var table = new Table(definition.TableName);
        table.AddColumn<Guid>(definition.IdColumnName).AsPrimaryKey();
        table.AddColumn(definition.JsonBodyColumnName, "BLOB").NotNull();
        if (definition.TimestampColumnName.IsNotEmpty())
        {
            table.AddColumn<DateTimeOffset>(definition.TimestampColumnName)
                .DefaultValueByExpression("SYSTIMESTAMP AT TIME ZONE ''UTC''");
        }

        if (definition.MessageTypeColumnName.IsNotEmpty())
        {
            table.AddColumn<string>(definition.MessageTypeColumnName);
        }

        return table;
    }
}
