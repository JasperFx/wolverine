using IntegrationTests;
using JasperFx;
using JasperFx.Core;
using Marten;
using Microsoft.Extensions.Hosting;
using Wolverine.ComplianceTests.Partitioning;
using Wolverine.Marten;
using Wolverine.Tracking;
using Xunit;

namespace Wolverine.Pubsub.Tests;

// GH-3467: end-to-end coverage for the GCP Pub/Sub global partitioning topology against the emulator.
public class global_partitioned_sharded_processing : IAsyncLifetime
{
    private IHost _host = null!;

    public async Task InitializeAsync()
    {
        _host = await Host.CreateDefaultBuilder()
            .UseWolverine(opts =>
            {
                opts.Durability.Mode = DurabilityMode.Solo;

                opts.UsePubsubTesting().AutoProvision().AutoPurgeOnStartup();

                opts.Services.AddMarten(m =>
                {
                    m.Connection(Servers.PostgresConnectionString);
                    m.DatabaseSchemaName = "gletters_pubsub";
                    m.DisableNpgsqlLogging = true;
                }).IntegrateWithWolverine();

                opts.UseShardedLetters(topology => topology.UseShardedPubsubTopics("gletters", 4));
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
