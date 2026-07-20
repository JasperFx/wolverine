using IntegrationTests;
using JasperFx.Core;
using Microsoft.Extensions.Hosting;
using Npgsql;
using Shouldly;
using Weasel.Postgresql;
using Wolverine;
using Wolverine.ComplianceTests;
using Wolverine.Persistence.Durability;
using Wolverine.Postgresql;
using Wolverine.Postgresql.Transport;
using Wolverine.Runtime;
using Wolverine.Tracking;

namespace PostgresqlTests.Transport;

public class reset_clears_transport_queue_tables : PostgresqlContext, IAsyncLifetime
{
    private IHost theHost = null!;
    private PostgresqlQueue theQueue = null!;
    private IMessageStore theMessageStore = null!;

    public async Task InitializeAsync()
    {
        using (var conn = new NpgsqlConnection(Servers.PostgresConnectionString))
        {
            await conn.OpenAsync();
            await conn.DropSchemaAsync("reset_transports");
            await conn.CloseAsync();
        }

        theHost = await Host.CreateDefaultBuilder()
            .UseWolverine(opts =>
            {
                opts.UsePostgresqlPersistenceAndTransport(Servers.PostgresConnectionString,
                    schema: "reset_transports", transportSchema: "reset_transports");

                // Neutralize the host's auto-started listener so it cannot drain the queue
                // mid-test before the reset runs (mirrors the basic_functionality fixture).
                opts.ListenToPostgresqlQueue("resetone").PollingInterval(1.Hours());
            }).StartAsync();

        var transport = theHost.GetRuntime().Options.Transports.GetOrCreate<PostgresqlTransport>();
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
        // otherwise integration tests over the Postgres queue transport carry rows between runs.
        await theMessageStore.Admin.ClearAllAsync();

        (await theQueue.CountAsync()).ShouldBe(0);
        (await theQueue.ScheduledCountAsync()).ShouldBe(0);
    }
}
