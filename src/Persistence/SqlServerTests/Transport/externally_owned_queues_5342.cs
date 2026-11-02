using IntegrationTests;
using JasperFx;
using JasperFx.Core;
using JasperFx.Resources;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Hosting;
using Shouldly;
using Weasel.SqlServer;
using Wolverine;
using Wolverine.Runtime;
using Wolverine.SqlServer;
using Wolverine.Tracking;
using Xunit;

namespace SqlServerTests.Transport;

/// <summary>
/// GH-5342. A SQL Server queue whose tables belong to another application must never be created, migrated or
/// dropped by this one, even with AutoProvision() on. That includes `db-apply` and migrations, which see the queue tables as Weasel feature schemas.
/// </summary>
public class externally_owned_queues_5342 : IAsyncLifetime
{
    private const string SchemaName = "ext_owned_5342";

    public async ValueTask InitializeAsync()
    {
        await using var conn = new SqlConnection(Servers.SqlServerConnectionString);
        await conn.OpenAsync(TestContext.Current.CancellationToken);
        await conn.DropSchemaAsync(SchemaName, TestContext.Current.CancellationToken);
        await conn.CloseAsync();
    }

    public ValueTask DisposeAsync()
    {

        return ValueTask.CompletedTask;
    }

    private static async Task<string[]> queueTablesAsync()
    {
        await using var conn = new SqlConnection(Servers.SqlServerConnectionString);
        await conn.OpenAsync(TestContext.Current.CancellationToken);

        var tables = new List<string>();
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = "select TABLE_NAME from INFORMATION_SCHEMA.TABLES where TABLE_SCHEMA = '" + SchemaName + "' and TABLE_NAME like 'wolverine_queue_%'";
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
                opts.UseSqlServerPersistenceAndTransport(Servers.SqlServerConnectionString, SchemaName, transportSchema: SchemaName).AutoProvision();

                opts.ListenToSqlServerQueue("owned");

                opts.PublishMessage<ExternallyOwnedQueueMessage>()
                    .ToSqlServerQueue("foreign")
                    .ExternallyOwned();
            }).StartAsync(TestContext.Current.CancellationToken);

        await host.RebuildAllEnvelopeStorageAsync();
        await host.SetupResources(cancellation: TestContext.Current.CancellationToken);

        (await queueTablesAsync()).ShouldBe(["wolverine_queue_owned", "wolverine_queue_owned_scheduled"]);

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
                       opts.UseSqlServerPersistenceAndTransport(Servers.SqlServerConnectionString, SchemaName, transportSchema: SchemaName);
                       opts.ListenToSqlServerQueue("orders");
                   }).StartAsync(ct))
        {
            await owner.StopAsync(ct);
        }

        IHostBuilder configure() => Host.CreateDefaultBuilder()
            .UseWolverine(opts =>
            {
                opts.UseSqlServerPersistenceAndTransport(Servers.SqlServerConnectionString, SchemaName, transportSchema: SchemaName).AutoProvision();

                #region sample_sqlserver_queue_is_externally_owned

                // The "orders" queue tables belong to another application. Wolverine
                // reads from and sends to the queue, but never creates, migrates or
                // drops its tables, even with AutoProvision() on
                opts.ListenToSqlServerQueue("orders").ExternallyOwned();

                opts.PublishMessage<ExternallyOwnedQueueMessage>()
                    .ToSqlServerQueue("orders")
                    .ExternallyOwned();

                #endregion

                // Just to keep the test quick
                opts.ListenToSqlServerQueue("orders").PollingInterval(250.Milliseconds());

                // Owned by this application, so provisioned as usual
                opts.ListenToSqlServerQueue("replies");
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
            (await queueTablesAsync()).ShouldContain("wolverine_queue_orders");

            await host.StopAsync(ct);
        }

        // check still covers the externally owned queue, and it exists
        (await configure().RunJasperFxCommands(["resources", "check"])).ShouldBe(0);

        (await configure().RunJasperFxCommands(["resources", "teardown"])).ShouldBe(0);

        // What this application owns is gone, and what it doesn't is still there
        var tables = await queueTablesAsync();
        tables.ShouldContain("wolverine_queue_orders");
        tables.ShouldContain("wolverine_queue_orders_scheduled");
        tables.ShouldNotContain("wolverine_queue_replies");
    }
}

public record ExternallyOwnedQueueMessage(Guid Id);

public static class ExternallyOwnedQueueMessageHandler
{
    public static void Handle(ExternallyOwnedQueueMessage message)
    {
    }
}
