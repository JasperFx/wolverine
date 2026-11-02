using JasperFx;
using JasperFx.Core;
using JasperFx.Resources;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Hosting;
using Shouldly;
using Weasel.Sqlite;
using Wolverine;
using Wolverine.Runtime;
using Wolverine.Sqlite;
using Wolverine.Tracking;
using Xunit;

namespace SqliteTests.Transport;

/// <summary>
/// GH-5342. A SQLite queue whose tables belong to another application must never be created, migrated or
/// dropped by this one, even with AutoProvision() on. That includes the message store's own schema: the queue tables are registered with it, so
/// RebuildAllEnvelopeStorageAsync() and a migration would otherwise build or drop them too.
/// </summary>
public class externally_owned_queues_5342 : IAsyncLifetime
{
    private readonly SqliteTestDatabase _database = Servers.CreateDatabase(nameof(externally_owned_queues_5342));

    public async ValueTask InitializeAsync()
    {
        await Task.CompletedTask;
    }

    public ValueTask DisposeAsync()
    {
        _database.Dispose();
        return ValueTask.CompletedTask;
    }

    private async Task<string[]> queueTablesAsync()
    {
        using var dataSource = new SqliteDataSource(_database.ConnectionString);
        await using var conn = (SqliteConnection)await dataSource.OpenConnectionAsync(TestContext.Current.CancellationToken);

        var tables = await conn.ExistingTablesAsync(ct: TestContext.Current.CancellationToken);

        return tables.Select(x => x.Name).Where(x => x.StartsWith("wolverine_queue_")).OrderBy(x => x).ToArray();
    }

    [Fact]
    public async Task externally_owned_queue_tables_are_never_created()
    {
        using var host = await Host.CreateDefaultBuilder()
            .UseWolverine(opts =>
            {
                opts.UseSqlitePersistenceAndTransport(_database.ConnectionString).AutoProvision();

                opts.ListenToSqliteQueue("owned");

                opts.PublishMessage<ExternallyOwnedQueueMessage>()
                    .ToSqliteQueue("foreign")
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
                       opts.UseSqlitePersistenceAndTransport(_database.ConnectionString);
                       opts.ListenToSqliteQueue("orders");
                   }).StartAsync(ct))
        {
            await owner.StopAsync(ct);
        }

        IHostBuilder configure() => Host.CreateDefaultBuilder()
            .UseWolverine(opts =>
            {
                opts.UseSqlitePersistenceAndTransport(_database.ConnectionString).AutoProvision();

                #region sample_sqlite_queue_is_externally_owned

                // The "orders" queue tables belong to another application. Wolverine
                // reads from and sends to the queue, but never creates, migrates or
                // drops its tables, even with AutoProvision() on
                opts.ListenToSqliteQueue("orders").ExternallyOwned();

                opts.PublishMessage<ExternallyOwnedQueueMessage>()
                    .ToSqliteQueue("orders")
                    .ExternallyOwned();

                #endregion

                // Just to keep the test quick
                opts.ListenToSqliteQueue("orders").PollingInterval(250.Milliseconds());

                // Owned by this application, so provisioned as usual
                opts.ListenToSqliteQueue("replies");
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
