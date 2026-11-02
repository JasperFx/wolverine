using IntegrationTests;
using JasperFx;
using JasperFx.Core;
using JasperFx.Resources;
using Microsoft.Extensions.Hosting;
using Npgsql;
using Shouldly;
using Weasel.Postgresql;
using Wolverine;
using Wolverine.Postgresql;
using Wolverine.Runtime;
using Wolverine.Tracking;
using Xunit;

namespace PostgresqlTests.Transport;

/// <summary>
/// GH-5342. A PostgreSQL queue whose tables belong to another application -- the service that reads from it, or
/// the one that created it -- must never be created, migrated or dropped by this one, even with AutoProvision()
/// on. That includes the message store's own schema: the queue tables are registered with it, so
/// RebuildAllEnvelopeStorageAsync() and a migration would otherwise build or drop them too.
/// </summary>
public class externally_owned_queues_5342 : PostgresqlContext, IAsyncLifetime
{
    private const string SchemaName = "ext_owned_5342";

    public async ValueTask InitializeAsync()
    {
        await using var conn = new NpgsqlConnection(Servers.PostgresConnectionString);
        await conn.OpenAsync(TestContext.Current.CancellationToken);
        await conn.DropSchemaAsync(SchemaName, TestContext.Current.CancellationToken);
        await conn.CloseAsync();
    }

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;

    private static async Task<string[]> queueTablesAsync()
    {
        await using var conn = new NpgsqlConnection(Servers.PostgresConnectionString);
        await conn.OpenAsync(TestContext.Current.CancellationToken);
        var tables = await conn.ExistingTablesAsync(schemas: [SchemaName], ct: TestContext.Current.CancellationToken);
        await conn.CloseAsync();

        return tables.Select(x => x.Name).Where(x => x.StartsWith("wolverine_queue_")).OrderBy(x => x).ToArray();
    }

    [Fact]
    public async Task externally_owned_queue_tables_are_never_created()
    {
        using var host = await Host.CreateDefaultBuilder()
            .UseWolverine(opts =>
            {
                opts.UsePostgresqlPersistenceAndTransport(Servers.PostgresConnectionString, SchemaName,
                    transportSchema: SchemaName).AutoProvision();

                opts.ListenToPostgresqlQueue("owned");

                opts.PublishMessage<ExternallyOwnedQueueMessage>()
                    .ToPostgresqlQueue("foreign")
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
                       opts.UsePostgresqlPersistenceAndTransport(Servers.PostgresConnectionString, SchemaName,
                           transportSchema: SchemaName);
                       opts.ListenToPostgresqlQueue("orders");
                   }).StartAsync(ct))
        {
            await owner.StopAsync(ct);
        }

        IHostBuilder configure() => Host.CreateDefaultBuilder()
            .UseWolverine(opts =>
            {
                opts.UsePostgresqlPersistenceAndTransport(Servers.PostgresConnectionString, SchemaName,
                    transportSchema: SchemaName).AutoProvision();

                #region sample_postgresql_queue_is_externally_owned

                // The "orders" queue tables belong to another application. Wolverine
                // reads from and sends to the queue, but never creates, migrates or
                // drops its tables, even with AutoProvision() on
                opts.ListenToPostgresqlQueue("orders").ExternallyOwned();

                opts.PublishMessage<ExternallyOwnedQueueMessage>()
                    .ToPostgresqlQueue("orders")
                    .ExternallyOwned();

                #endregion

                // Just to keep the test quick
                opts.ListenToPostgresqlQueue("orders").PollingInterval(250.Milliseconds());

                // Owned by this application, so provisioned as usual
                opts.ListenToPostgresqlQueue("replies");
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
