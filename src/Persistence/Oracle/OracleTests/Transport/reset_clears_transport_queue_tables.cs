using IntegrationTests;
using JasperFx;
using JasperFx.Core;
using Microsoft.Extensions.Hosting;
using Oracle.ManagedDataAccess.Client;
using Shouldly;
using Weasel.Oracle;
using Wolverine;
using Wolverine.ComplianceTests;
using Wolverine.Oracle;
using Wolverine.Oracle.Transport;
using Wolverine.Persistence.Durability;
using Wolverine.Runtime;
using Wolverine.Tracking;

namespace OracleTests.Transport;

[Collection("oracle")]
public class reset_clears_transport_queue_tables : IAsyncLifetime
{
    private IHost theHost = null!;
    private OracleQueue theQueue = null!;
    private IMessageStore theMessageStore = null!;

    public async Task InitializeAsync()
    {
        // Clean any queue tables left behind by a prior run
        var dataSource = new OracleDataSource(Servers.OracleConnectionString);
        await using (var conn = await dataSource.OpenConnectionAsync())
        {
            foreach (var table in new[] { "WOLVERINE_QUEUE_RESETONE", "WOLVERINE_QUEUE_RESETONE_SCHEDULED" })
            {
                try
                {
                    await using var cmd = conn.CreateCommand($"DROP TABLE WOLVERINE.{table} CASCADE CONSTRAINTS");
                    await cmd.ExecuteNonQueryAsync();
                }
                catch (OracleException) { /* table doesn't exist */ }
            }

            await conn.CloseAsync();
        }

        theHost = await Host.CreateDefaultBuilder()
            .UseWolverine(opts =>
            {
                opts.PersistMessagesWithOracle(Servers.OracleConnectionString, "WOLVERINE")
                    .EnableMessageTransport();

                // Neutralize the host's auto-started listener so it cannot drain the queue
                // mid-test before the reset runs (mirrors the basic_functionality fixture).
                opts.ListenToOracleQueue("resetone").PollingInterval(1.Hours());
                opts.Durability.Mode = DurabilityMode.Solo;
            }).StartAsync();

        var transport = theHost.GetRuntime().Options.Transports.GetOrCreate<OracleTransport>();
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
        // otherwise integration tests over the Oracle queue transport carry rows between runs.
        await theMessageStore.Admin.ClearAllAsync();

        (await theQueue.CountAsync()).ShouldBe(0);
        (await theQueue.ScheduledCountAsync()).ShouldBe(0);
    }
}
