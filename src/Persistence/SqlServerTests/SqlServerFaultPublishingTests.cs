using IntegrationTests;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Wolverine;
using Wolverine.ComplianceTests.ErrorHandling.Faults;
using Wolverine.ErrorHandling;
using Wolverine.Persistence.Durability;
using Wolverine.RDBMS;
using Wolverine.SqlServer;

namespace SqlServerTests;

public class SqlServerFaultPublishingTests : DurableFaultPublishingCompliance
{
    private const string SchemaName = "fault_compliance";

    public override async Task<IHost> BuildCleanHostAsync(Action<WolverineOptions>? optionalCompose = null)
    {
        var host = await Host.CreateDefaultBuilder()
            .UseWolverine(opts =>
            {
                opts.PersistMessagesWithSqlServer(Servers.SqlServerConnectionString, SchemaName);

                opts.Durability.Mode = DurabilityMode.Solo;

                opts.Discovery.IncludeType<AlwaysFailsHandler>();
                opts.Discovery.IncludeType<FaultSinkHandler>();
                opts.Services.AddSingleton<FaultSink>();

                opts.OnException<Exception>().MoveToErrorQueue();
                opts.PublishFaultEvents();
                opts.Policies.UseDurableLocalQueues();

                optionalCompose?.Invoke(opts);
            }).StartAsync();

        var messageStore = host.Services.GetRequiredService<IMessageStore>();
        await messageStore.Admin.ClearAllAsync();

        return host;
    }

    protected override async Task<DurableSnapshot> SnapshotAsync(IHost host)
    {
        await using var conn = new SqlConnection(Servers.SqlServerConnectionString);
        await conn.OpenAsync();

        var dlqCount = await ScalarInt(conn,
            $"SELECT COUNT(*) FROM [{SchemaName}].{DatabaseConstants.DeadLetterTable}");

        // The Fault<T> is published from inside the failure-handler's outbox transaction.
        // For an in-process durable local queue (UseDurableLocalQueues), the envelope is
        // persisted via the inbox; for a remote durable destination it would land in the
        // outgoing table. Sum both tables so the atomicity assertion holds in either
        // routing topology.
        var faultEnvelopeCount = await ScalarInt(conn,
            $"SELECT " +
            $"  (SELECT COUNT(*) FROM [{SchemaName}].{DatabaseConstants.OutgoingTable} " +
            $"     WHERE {DatabaseConstants.MessageType} LIKE '%Fault%') " +
            $"+ (SELECT COUNT(*) FROM [{SchemaName}].{DatabaseConstants.IncomingTable} " +
            $"     WHERE {DatabaseConstants.MessageType} LIKE '%Fault%')");

        return new DurableSnapshot(dlqCount, faultEnvelopeCount);
    }

    private static async Task<int> ScalarInt(SqlConnection conn, string sql)
    {
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = sql;
        var result = await cmd.ExecuteScalarAsync();
        return Convert.ToInt32(result);
    }
}
