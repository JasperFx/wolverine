using IntegrationTests;
using JasperFx.Core;
using Microsoft.Extensions.Hosting;
using MySqlConnector;
using Shouldly;
using Wolverine;
using Wolverine.ComplianceTests;
using Wolverine.MySql;
using Wolverine.MySql.Transport;
using Wolverine.Persistence.Durability;
using Wolverine.Runtime;
using Wolverine.Tracking;

namespace MySqlTests.Transport;

[Collection("mysql")]
public class reset_clears_transport_queue_tables : IAsyncLifetime
{
    private IHost theHost = null!;
    private MySqlQueue theQueue = null!;
    private IMessageStore theMessageStore = null!;

    public async Task InitializeAsync()
    {
        await using (var conn = new MySqlConnection(Servers.MySqlConnectionString))
        {
            await conn.OpenAsync();
            await using var dropCmd = conn.CreateCommand();
            dropCmd.CommandText =
                "DROP DATABASE IF EXISTS reset_transports; CREATE DATABASE reset_transports;";
            await dropCmd.ExecuteNonQueryAsync();
            await conn.CloseAsync();
        }

        theHost = await Host.CreateDefaultBuilder()
            .UseWolverine(opts =>
            {
                opts.UseMySqlPersistenceAndTransport(Servers.MySqlConnectionString, schema: "reset_transports",
                    transportSchema: "reset_transports");

                // Neutralize the host's auto-started listener so it cannot drain the queue
                // mid-test before the reset runs (mirrors the basic_functionality fixture).
                opts.ListenToMySqlQueue("resetone").PollingInterval(1.Hours());
            }).StartAsync();

        var transport = theHost.GetRuntime().Options.Transports.GetOrCreate<MySqlTransport>();
        theQueue = transport.Queues["resetone"];
        theMessageStore = theHost.GetRuntime().Storage;
    }

    public async Task DisposeAsync()
    {
        await theHost.StopAsync();
        theHost.Dispose();
    }

    [Fact]
    public async Task clear_all_async_empties_the_transport_queue_tables()
    {
        // Row in the queue table
        var immediate = ObjectMother.Envelope();
        immediate.DeliverBy = DateTimeOffset.UtcNow.AddHours(1);
        await theQueue.SendAsync(immediate);

        // Row in the scheduled-message table
        var scheduled = ObjectMother.Envelope();
        scheduled.ScheduleDelay = 1.Hours();
        scheduled.DeliverBy = DateTimeOffset.UtcNow.AddHours(1);
        await theQueue.SendAsync(scheduled);

        (await theQueue.CountAsync()).ShouldBe(1);
        (await theQueue.ScheduledCountAsync()).ShouldBe(1);

        // The message-store reset must also clear the registered transport queue tables,
        // otherwise integration tests over the MySQL queue transport carry rows between runs.
        await theMessageStore.Admin.ClearAllAsync();

        (await theQueue.CountAsync()).ShouldBe(0);
        (await theQueue.ScheduledCountAsync()).ShouldBe(0);
    }
}
