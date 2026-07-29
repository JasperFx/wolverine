using IntegrationTests;
using JasperFx;
using JasperFx.Core;
using Microsoft.Extensions.Hosting;
using Shouldly;
using Wolverine.ComplianceTests.Partitioning;
using Wolverine.Configuration;
using Wolverine.Nats.Internal;
using Wolverine.Postgresql;
using Wolverine.Runtime;
using Wolverine.Tracking;
using Xunit;

namespace Wolverine.Nats.Tests;

// GH-3467: end-to-end coverage for the NATS global partitioning topology.
[Collection("NATS Integration")]
public class global_partitioned_sharded_processing : IAsyncLifetime
{
    private readonly NatsContainerFixture _fixture;
    private IHost _host = null!;

    public global_partitioned_sharded_processing(NatsContainerFixture fixture)
    {
        _fixture = fixture;
    }

    public async ValueTask InitializeAsync()
    {
        _host = await Host.CreateDefaultBuilder()
            .UseWolverine(opts =>
            {
                opts.Durability.Mode = DurabilityMode.Solo;

                opts.UseNats(_fixture.ConnectionString);

                // The global partitioning topology forces every slot into durable mode, so the
                // host needs a real message store
                opts.PersistMessagesWithPostgresql(Servers.PostgresConnectionString, "gletters_nats");

                opts.UseShardedLetters(topology => topology.UseShardedNatsSubjects("gletters", 4));
            }).StartAsync();
    }

    public async ValueTask DisposeAsync()
    {
        await _host.StopAsync();
        _host.Dispose();
    }

    // GH-3467: global partitioning forces EndpointMode.Durable on every slot, and a NatsEndpoint only
    // supports Durable when it is JetStream-backed -- so UseShardedNatsSubjects() has to turn JetStream
    // on and declare the backing stream itself. Before the fix this configuration threw outright.
    [Fact]
    public void every_shard_endpoint_is_jetstream_backed_and_durable()
    {
        var endpoints = _host.GetRuntime().Options.Transports
            .AllEndpoints()
            .OfType<NatsEndpoint>()
            .Where(x => x.Subject.StartsWith("gletters"))
            .ToArray();

        endpoints.Length.ShouldBe(4);

        foreach (var endpoint in endpoints)
        {
            endpoint.UseJetStream.ShouldBeTrue();
            endpoint.StreamName.ShouldNotBeNull();
            endpoint.Mode.ShouldBe(EndpointMode.Durable);
        }
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
