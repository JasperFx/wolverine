using IntegrationTests;
using JasperFx.Core;
using Microsoft.Extensions.Hosting;
using Shouldly;
using Wolverine;
using Wolverine.Oracle;
using Wolverine.Oracle.Transport;
using Wolverine.Tracking;

namespace OracleTests.Transport;

/// <summary>
/// GH-5192. From 7.0 an Oracle queue table Wolverine creates gets the queue_order tie-breaker, but a queue table
/// that already exists without it keeps its layout: adding an identity numbers every row. The listener then
/// dequeues that table in timestamp order.
/// </summary>
[Collection("oracle")]
public class queue_tables_new_only
{
    private static CancellationToken ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task a_queue_table_wolverine_creates_gets_deterministic_ordering_by_default()
    {
        var queueName = newQueueName();
        IHost? host = null;
        try
        {
            host = await startAsync(queueName, _ => { });
            var queue = queueOf(host, queueName);

            queue.Parent.EnableDeterministicQueueOrdering.ShouldBeTrue();
            await queue_ordering_is_opt_in.assertQueueSchemaAsync(queue.QueueTable, ordered: true);
            await queue_ordering_is_opt_in.assertQueueSchemaAsync(queue.ScheduledTable, ordered: true);
            (await queue.CheckAsync()).ShouldBeTrue();
        }
        finally
        {
            await queue_ordering_is_opt_in.stopAndDropAsync(host, queueName);
        }
    }

    [Fact]
    public async Task an_existing_classic_queue_table_is_adopted_as_it_stands()
    {
        var queueName = newQueueName();
        IHost? host = null;
        try
        {
            // A 6.x application's classic tables
            using (var classic = await startAsync(queueName, x => x.DisableDeterministicQueueOrdering()))
            {
                await classic.StopAsync(ct);
            }

            // The same database after the upgrade, on the 7.0 default
            host = await startAsync(queueName, _ => { });
            var queue = queueOf(host, queueName);
            queue.Parent.EnableDeterministicQueueOrdering.ShouldBeTrue();

            await host.GetRuntime().Storage.Admin.MigrateAsync();
            await queue_ordering_is_opt_in.assertQueueSchemaAsync(queue.QueueTable, ordered: false);
            await queue_ordering_is_opt_in.assertQueueSchemaAsync(queue.ScheduledTable, ordered: false);
            (await queue.CheckAsync()).ShouldBeTrue();

            var source = queue.Parent.Store!.OracleDataSource;
            (await queue.UsesQueueOrderingAsync("check", source)).ShouldBeFalse();
        }
        finally
        {
            await queue_ordering_is_opt_in.stopAndDropAsync(host, queueName);
        }
    }

    private static string newQueueName() => $"NO_{Guid.NewGuid().ToString("N")[..10]}".ToUpperInvariant();

    private static async Task<IHost> startAsync(string queueName, Action<OraclePersistenceExpression> transport)
    {
        return await Host.CreateDefaultBuilder()
            .UseWolverine(opts =>
            {
                opts.Durability.Mode = DurabilityMode.Solo;
                opts.Durability.ScheduledJobFirstExecution = 1.Hours();
                opts.Durability.ScheduledJobPollingTime = 1.Hours();

                opts.PersistMessagesWithOracle(Servers.OracleConnectionString, "WOLVERINE")
                    .EnableMessageTransport(transport);

                opts.ListenToOracleQueue(queueName).PollingInterval(1.Hours()).MaximumMessagesToReceive(0);
            }).StartAsync(ct);
    }

    private static OracleQueue queueOf(IHost host, string queueName)
    {
        return host.GetRuntime().Options.Transports.GetOrCreate<OracleTransport>().Queues[queueName];
    }
}
