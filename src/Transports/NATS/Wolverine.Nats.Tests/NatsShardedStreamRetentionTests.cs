using IntegrationTests;
using JasperFx.Core;
using Microsoft.Extensions.Hosting;
using NATS.Client.Core;
using NATS.Client.JetStream;
using NATS.Client.JetStream.Models;
using NATS.Net;
using Shouldly;
using Wolverine.ComplianceTests.Partitioning;
using Wolverine.Nats.Internal;
using Wolverine.Postgresql;
using Wolverine.Tracking;
using Wolverine.Transports;
using Xunit;

namespace Wolverine.Nats.Tests;

/// <summary>
/// <c>UseShardedNatsSubjects()</c> declares a stream per shard unless one of the same name is declared already.
/// Each shard has exactly one consuming node at a time, so the stream has to keep messages until they are
/// acknowledged even while no consumer is bound -- at startup, or while a shard moves to another node. That is
/// JetStream's work-queue retention. Interest retention, which <c>StreamConfiguration.AsWorkQueue()</c> sets,
/// discards a message on arrival when no consumer is interested.
/// </summary>
[Collection("NATS Integration")]
[Trait("Category", "Integration")]
public class NatsShardedStreamRetentionTests
{
    private readonly NatsContainerFixture _fixture;
    private readonly ITestOutputHelper _output;

    public NatsShardedStreamRetentionTests(NatsContainerFixture fixture, ITestOutputHelper output)
    {
        _fixture = fixture;
        _output = output;
    }

    [Fact]
    public async Task sharded_stream_uses_workqueue_retention()
    {
        var baseName = $"wqshards{Guid.NewGuid():N}"[..20];

        using var host = await startAsync(baseName, _ => { });

        await using var connection = await connectAsync();
        var js = connection.CreateJetStreamContext();

        for (var shard = 1; shard <= 2; shard++)
        {
            var stream = await js.GetStreamAsync($"{baseName}{shard}".ToUpperInvariant(),
                cancellationToken: TestContext.Current.CancellationToken);
            stream.Info.Config.Retention.ShouldBe(StreamConfigRetention.Workqueue);
        }
    }

    [Fact]
    public async Task declared_stream_wins_over_the_shard_default()
    {
        var baseName = $"declshards{Guid.NewGuid():N}"[..20];
        var declared = $"{baseName}1".ToUpperInvariant();

        using var host = await startAsync(baseName,
            nats => nats.DefineStream(declared, s => s.WithSubjects($"{baseName}1").WithReplicas(1)));

        await using var connection = await connectAsync();
        var js = connection.CreateJetStreamContext();

        (await js.GetStreamAsync(declared, cancellationToken: TestContext.Current.CancellationToken))
            .Info.Config.Retention.ShouldBe(StreamConfigRetention.Limits);
        (await js.GetStreamAsync($"{baseName}2".ToUpperInvariant(),
                cancellationToken: TestContext.Current.CancellationToken))
            .Info.Config.Retention.ShouldBe(StreamConfigRetention.Workqueue);
    }

    [Fact]
    public async Task shard_listener_restarts_on_its_workqueue_stream()
    {
        var baseName = $"rsshards{Guid.NewGuid():N}"[..20];

        using var host = await startAsync(baseName, _ => { });

        var runtime = host.GetRuntime();
        var endpoint = runtime.Options.Transports.GetOrCreate<NatsTransport>().EndpointForSubject($"{baseName}1");
        var circuit = runtime.Endpoints.FindListenerCircuit(endpoint.Uri).ShouldNotBeNull();
        circuit.Status.ShouldBe(ListeningStatus.Accepting);

        // What a back-pressure pause or a shard moving between nodes does: stop the listener and start a new one
        // right away. A work-queue stream refuses a second consumer with an overlapping filter, so the new
        // listener must not collide with the consumer the old one leaves behind.
        await circuit.RestartAsync();
        circuit.Status.ShouldBe(ListeningStatus.Accepting);

        var tracked = await host
            .TrackActivity()
            .IncludeExternalTransports()
            .Timeout(60.Seconds())
            .ExecuteAndWaitAsync(ShardedProcessing.PumpOutLetters);

        ShardedProcessing.AssertEveryShardWasUsed(tracked, baseName, 2);
    }

    private async Task<IHost> startAsync(string baseName, Action<NatsTransportExpression> configure)
    {
        return await Host.CreateDefaultBuilder()
            .ConfigureLogging(l => l.AddXunitLogging(_output))
            .UseWolverine(opts =>
            {
                opts.ServiceName = "ShardRetention";
                opts.Durability.Mode = DurabilityMode.Solo;

                configure(opts.UseNats(_fixture.ConnectionString));

                // The global partitioning topology forces durable mode on every slot
                opts.PersistMessagesWithPostgresql(Servers.PostgresConnectionString, "nats_shard_retention");

                opts.UseShardedLetters(topology => topology.UseShardedNatsSubjects(baseName, 2));
            })
            .StartAsync(TestContext.Current.CancellationToken);
    }

    private async Task<NatsConnection> connectAsync()
    {
        var connection = new NatsConnection(new NatsOpts { Url = _fixture.ConnectionString });
        await connection.ConnectAsync();
        return connection;
    }
}
