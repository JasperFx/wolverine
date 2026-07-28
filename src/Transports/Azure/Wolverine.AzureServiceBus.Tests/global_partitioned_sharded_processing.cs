using IntegrationTests;
using JasperFx;
using JasperFx.Core;
using Marten;
using Microsoft.Extensions.Hosting;
using Wolverine.ComplianceTests.Partitioning;
using Wolverine.Marten;
using Wolverine.Tracking;
using Xunit;

namespace Wolverine.AzureServiceBus.Tests;

// GH-3467: the global partitioning topology shipped for Azure Service Bus with no end-to-end
// coverage. Deliberately the minimal assertion -- every slot gets used and a group id never
// straddles two slots -- rather than a clone of the deeper Kafka-only suites.
public class global_partitioned_sharded_processing : IAsyncLifetime
{
    private IHost _host = null!;

    public async Task InitializeAsync()
    {
        _host = await Host.CreateDefaultBuilder()
            .UseWolverine(opts =>
            {
                opts.Durability.Mode = DurabilityMode.Solo;

                opts.UseAzureServiceBusTesting().AutoProvision().AutoPurgeOnStartup();

                opts.Services.AddMarten(m =>
                {
                    m.Connection(Servers.PostgresConnectionString);
                    m.DatabaseSchemaName = "gletters_asb";
                    m.DisableNpgsqlLogging = true;
                }).IntegrateWithWolverine();

                opts.UseShardedLetters(topology => topology.UseShardedAzureServiceBusQueues("gletters", 4));
            }).StartAsync();
    }

    public async Task DisposeAsync()
    {
        await _host.StopAsync();
        _host.Dispose();
    }

    [Fact]
    public async Task global_partitioned_processing_spreads_across_every_slot()
    {
        var tracked = await _host
            .TrackActivity()
            .IncludeExternalTransports()
            .Timeout(120.Seconds())
            .ExecuteAndWaitAsync(ShardedProcessing.PumpOutLetters);

        ShardedProcessing.AssertEveryShardWasUsed(tracked, "gletters", 4);
        ShardedProcessing.AssertGroupsNeverStraddleSlots();
    }
}
