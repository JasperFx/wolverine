using IntegrationTests;
using JasperFx.Core;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Wolverine;
using Wolverine.ComplianceTests.ErrorHandling.Faults;
using Wolverine.ErrorHandling;
using Wolverine.RDBMS;
using Wolverine.SqlServer;
using Wolverine.Util;

namespace SqlServerTests;

[Collection("sqlserver")]
public class SqlServerFaultPublishingTests : DurableFaultPublishingCompliance
{
    private const string SchemaName = "fault_compliance";

    public override async Task<IHost> BuildCleanHostAsync(Action<WolverineOptions>? optionalCompose = null)
    {
        await DropSchemaTablesAsync();

        var host = await Host.CreateDefaultBuilder()
            .UseWolverine(opts =>
            {
                opts.PersistMessagesWithSqlServer(Servers.SqlServerConnectionString, SchemaName);

                opts.Durability.Mode = DurabilityMode.Solo;
                opts.Durability.KeepAfterMessageHandling = 5.Minutes();

                opts.Discovery.IncludeType<AlwaysFailsHandler>();
                opts.Discovery.IncludeType<FaultSinkHandler>();
                opts.Services.AddSingleton<FaultSink>();

                opts.OnException<Exception>().MoveToErrorQueue();
                opts.PublishFaultEvents();
                opts.Policies.UseDurableLocalQueues();

                optionalCompose?.Invoke(opts);
            }).StartAsync();

        return host;
    }

    private static async Task DropSchemaTablesAsync()
    {
        await using var conn = new SqlConnection(Servers.SqlServerConnectionString);
        await conn.OpenAsync();
        await using var cmd = conn.CreateCommand();
        // Drop every table in the target schema; Wolverine recreates the ones it needs
        // during host startup. Foreign keys must be dropped first (Wolverine's
        // wolverine_node_assignments references wolverine_nodes). Idempotent — no-ops
        // on first run when the schema doesn't exist yet.
        cmd.CommandText = @"
            DECLARE @sql nvarchar(max) = N'';

            -- Drop all foreign keys in the schema first
            SELECT @sql += N'ALTER TABLE [' + s.name + N'].[' + t.name + N'] DROP CONSTRAINT [' + fk.name + N'];'
            FROM sys.foreign_keys fk
            INNER JOIN sys.tables t ON fk.parent_object_id = t.object_id
            INNER JOIN sys.schemas s ON t.schema_id = s.schema_id
            WHERE s.name = @schemaName;

            -- Then drop all tables
            SELECT @sql += N'DROP TABLE IF EXISTS [' + s.name + N'].[' + t.name + N'];'
            FROM sys.tables t
            INNER JOIN sys.schemas s ON t.schema_id = s.schema_id
            WHERE s.name = @schemaName;

            EXEC sp_executesql @sql;";
        cmd.Parameters.AddWithValue("@schemaName", SchemaName);
        await cmd.ExecuteNonQueryAsync();
    }

    protected override async Task<DurableSnapshot> SnapshotAsync(IHost host)
    {
        await using var conn = new SqlConnection(Servers.SqlServerConnectionString);
        await conn.OpenAsync();

        var dlqCount = await ScalarInt(conn,
            $"SELECT COUNT(*) FROM [{SchemaName}].{DatabaseConstants.DeadLetterTable}",
            parameters: null);

        // The Fault<T> is published from inside the failure-handler's outbox transaction.
        // For an in-process durable local queue (UseDurableLocalQueues), the envelope is
        // persisted via the inbox; for a remote durable destination it would land in the
        // outgoing table. Sum both tables so the atomicity assertion holds in either
        // routing topology.
        var faultTypeName = typeof(Fault<OrderPlaced>).ToMessageTypeName();
        var faultEnvelopeCount = await ScalarInt(conn,
            $"SELECT " +
            $"  (SELECT COUNT(*) FROM [{SchemaName}].{DatabaseConstants.OutgoingTable} " +
            $"     WHERE {DatabaseConstants.MessageType} = @msgType) " +
            $"+ (SELECT COUNT(*) FROM [{SchemaName}].{DatabaseConstants.IncomingTable} " +
            $"     WHERE {DatabaseConstants.MessageType} = @msgType)",
            parameters: ("@msgType", faultTypeName));

        return new DurableSnapshot(dlqCount, faultEnvelopeCount);
    }

    private static async Task<int> ScalarInt(SqlConnection conn, string sql,
        (string Name, object Value)? parameters)
    {
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = sql;
        if (parameters.HasValue)
        {
            cmd.Parameters.AddWithValue(parameters.Value.Name, parameters.Value.Value);
        }
        var result = await cmd.ExecuteScalarAsync();
        return Convert.ToInt32(result);
    }
}
