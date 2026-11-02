using IntegrationTests;
using JasperFx;
using JasperFx.Core;
using JasperFx.Resources;
using Microsoft.Extensions.Hosting;
using Oracle.ManagedDataAccess.Client;
using Shouldly;
using Wolverine;
using Wolverine.Oracle;
using Wolverine.Runtime;
using Wolverine.Tracking;
using Xunit;

namespace OracleTests.Transport;

/// <summary>
/// GH-5342. An Oracle queue whose tables belong to another application must never be created, migrated or
/// dropped by this one, even with AutoProvision() on. That includes the message store's own schema: the queue
/// tables are registered with it, so RebuildAllEnvelopeStorageAsync() and a migration would otherwise build or
/// drop them too.
/// </summary>
public class externally_owned_queues_5342 : IAsyncLifetime
{
    // The transport's queue tables live in its own schema, shared with the other Oracle transport tests,
    // so these tests keep to their own queue names
    private const string QueueSchema = "WOLVERINE_QUEUES";
    private const string Prefix = "WOLVERINE_QUEUE_EXT5342_";

    public async ValueTask InitializeAsync()
    {
        foreach (var table in await queueTablesAsync())
        {
            await using var conn = new OracleConnection(Servers.OracleConnectionString);
            await conn.OpenAsync(TestContext.Current.CancellationToken);
            await using var cmd = conn.CreateCommand();
            cmd.CommandText = $"DROP TABLE {QueueSchema}.{table} CASCADE CONSTRAINTS";
            await cmd.ExecuteNonQueryAsync(TestContext.Current.CancellationToken);
            await conn.CloseAsync();
        }
    }

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;

    private static async Task<string[]> queueTablesAsync()
    {
        await using var conn = new OracleConnection(Servers.OracleConnectionString);
        await conn.OpenAsync(TestContext.Current.CancellationToken);

        var tables = new List<string>();
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = $"select table_name from all_tables where owner = '{QueueSchema}' and table_name like '{Prefix}%'";
        await using var reader = await cmd.ExecuteReaderAsync(TestContext.Current.CancellationToken);
        while (await reader.ReadAsync(TestContext.Current.CancellationToken))
        {
            tables.Add(reader.GetString(0));
        }

        await conn.CloseAsync();

        return tables.OrderBy(x => x).ToArray();
    }

    [Fact]
    public async Task externally_owned_queue_tables_are_never_created()
    {
        using var host = await Host.CreateDefaultBuilder()
            .UseWolverine(opts =>
            {
                opts.PersistMessagesWithOracle(Servers.OracleConnectionString, "WOLVERINE")
                    .EnableMessageTransport(t => t.AutoProvision());
                opts.Durability.Mode = DurabilityMode.Solo;

                opts.ListenToOracleQueue("ext5342_owned");

                opts.PublishMessage<ExternallyOwnedQueueMessage>()
                    .ToOracleQueue("ext5342_foreign")
                    .ExternallyOwned();
            }).StartAsync(TestContext.Current.CancellationToken);

        await host.RebuildAllEnvelopeStorageAsync();
        await host.SetupResources(cancellation: TestContext.Current.CancellationToken);

        (await queueTablesAsync()).ShouldBe([Prefix + "OWNED", Prefix + "OWNED_SCHEDULED"]);

        await host.StopAsync(TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task an_externally_owned_queue_is_used_but_never_dropped()
    {
        var ct = TestContext.Current.CancellationToken;

        // The owning application creates the "orders" queue
        using (var owner = await Host.CreateDefaultBuilder()
                   .UseWolverine(opts =>
                   {
                       opts.PersistMessagesWithOracle(Servers.OracleConnectionString, "WOLVERINE")
                           .EnableMessageTransport();
                       opts.Durability.Mode = DurabilityMode.Solo;
                       opts.ListenToOracleQueue("ext5342_orders");
                   }).StartAsync(ct))
        {
            await owner.StopAsync(ct);
        }

        IHostBuilder configure() => Host.CreateDefaultBuilder()
            .UseWolverine(opts =>
            {
                opts.PersistMessagesWithOracle(Servers.OracleConnectionString, "WOLVERINE")
                    .EnableMessageTransport(t => t.AutoProvision());
                opts.Durability.Mode = DurabilityMode.Solo;

                #region sample_oracle_queue_is_externally_owned

                // The "orders" queue tables belong to another application. Wolverine
                // reads from and sends to the queue, but never creates, migrates or
                // drops its tables, even with AutoProvision() on
                opts.ListenToOracleQueue("ext5342_orders").ExternallyOwned();

                opts.PublishMessage<ExternallyOwnedQueueMessage>()
                    .ToOracleQueue("ext5342_orders")
                    .ExternallyOwned();

                #endregion

                // Just to keep the test quick
                opts.ListenToOracleQueue("ext5342_orders").PollingInterval(250.Milliseconds());

                // Owned by this application, so provisioned as usual
                opts.ListenToOracleQueue("ext5342_replies");
            });

        using (var host = await configure().StartAsync(ct))
        {
            var tracked = await host.TrackActivity()
                .IncludeExternalTransports()
                .Timeout(30.Seconds())
                .SendMessageAndWaitAsync(new ExternallyOwnedQueueMessage(Guid.NewGuid()));

            tracked.Received.SingleMessage<ExternallyOwnedQueueMessage>().ShouldNotBeNull();

            // Rebuilding the message store must leave the other application's tables alone
            await host.RebuildAllEnvelopeStorageAsync();
            (await queueTablesAsync()).ShouldContain(Prefix + "ORDERS");

            await host.StopAsync(ct);
        }

        // check still covers the externally owned queue, and it exists
        (await configure().RunJasperFxCommands(["resources", "check"])).ShouldBe(0);

        (await configure().RunJasperFxCommands(["resources", "teardown"])).ShouldBe(0);

        // What this application owns is gone, and what it doesn't is still there
        var tables = await queueTablesAsync();
        tables.ShouldContain(Prefix + "ORDERS");
        tables.ShouldContain(Prefix + "ORDERS_SCHEDULED");
        tables.ShouldNotContain(Prefix + "REPLIES");
    }
}

public record ExternallyOwnedQueueMessage(Guid Id);

public static class ExternallyOwnedQueueMessageHandler
{
    public static void Handle(ExternallyOwnedQueueMessage message)
    {
    }
}
