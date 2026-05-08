using IntegrationTests;
using JasperFx.Core;
using Marten;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Npgsql;
using Wolverine;
using Wolverine.ComplianceTests.ErrorHandling.Faults;
using Wolverine.ErrorHandling;
using Wolverine.Marten;
using Wolverine.RDBMS;
using Wolverine.Util;

namespace MartenTests;

[Collection("marten")]
public class MartenFaultPublishingTests : DurableFaultPublishingCompliance
{
    private const string SchemaName = "fault_compliance";

    public override async Task<IHost> BuildCleanHostAsync(Action<WolverineOptions>? optionalCompose = null)
    {
        await DropSchemaAsync();

        var host = await Host.CreateDefaultBuilder()
            .UseWolverine(opts =>
            {
                opts.Services.AddMarten(x =>
                {
                    x.Connection(Servers.PostgresConnectionString);
                    x.DatabaseSchemaName = SchemaName;
                }).IntegrateWithWolverine();

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

    private static async Task DropSchemaAsync()
    {
        await using var conn = new NpgsqlConnection(Servers.PostgresConnectionString);
        await conn.OpenAsync();
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = $"DROP SCHEMA IF EXISTS {SchemaName} CASCADE";
        await cmd.ExecuteNonQueryAsync();
    }

    protected override async Task<DurableSnapshot> SnapshotAsync(IHost host)
    {
        await using var conn = new NpgsqlConnection(Servers.PostgresConnectionString);
        await conn.OpenAsync();

        var dlqCount = await ScalarInt(conn,
            $"SELECT COUNT(*) FROM {SchemaName}.{DatabaseConstants.DeadLetterTable}",
            parameters: null);

        // The Fault<T> is published from inside the failure-handler's outbox transaction.
        // For an in-process durable local queue (UseDurableLocalQueues), the envelope is
        // persisted via the inbox; for a remote durable destination it would land in the
        // outgoing table. Sum both tables so the atomicity assertion holds in either
        // routing topology.
        var faultTypeName = typeof(Fault<OrderPlaced>).ToMessageTypeName();
        var faultEnvelopeCount = await ScalarInt(conn,
            $"SELECT " +
            $"  (SELECT COUNT(*) FROM {SchemaName}.{DatabaseConstants.OutgoingTable} " +
            $"     WHERE {DatabaseConstants.MessageType} = @msgType) " +
            $"+ (SELECT COUNT(*) FROM {SchemaName}.{DatabaseConstants.IncomingTable} " +
            $"     WHERE {DatabaseConstants.MessageType} = @msgType)",
            parameters: ("@msgType", faultTypeName));

        return new DurableSnapshot(dlqCount, faultEnvelopeCount);
    }

    private static async Task<int> ScalarInt(NpgsqlConnection conn, string sql,
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
