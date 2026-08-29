using Confluent.Kafka;
using Confluent.Kafka.Admin;
using JasperFx.Core;
using JasperFx.Resources;
using Microsoft.Extensions.Hosting;
using Shouldly;
using Wolverine.Configuration;
using Wolverine.Kafka.Internals;
using Wolverine.Tracking;
using Wolverine.Transports;

namespace Wolverine.Kafka.Tests;

// GH-3473 heritage: the Dekaf client speaks only the KIP-848 next-generation consumer rebalance
// protocol (group.protocol=consumer), so every Wolverine listener joins its group through it — no
// opt-in, no classic-protocol conflicts to sanitize. These tests prove the group really joins via
// KIP-848 against the docker-compose Kafka 4.x broker (confluentinc/confluent-local:8.0.x) using a
// raw Confluent admin client as an independent oracle — `docker compose up -d kafka`.
public class next_generation_rebalance_protocol
{
    [Fact]
    public async Task end_to_end_round_trip_joins_the_group_through_kip848()
    {
        using var host = await Host.CreateDefaultBuilder()
            .UseWolverine(opts =>
            {
                opts.ServiceName = "kip848-e2e";
                opts.UseKafka(KafkaContainerFixture.ConnectionString)
                    .AutoProvision();
                opts.Policies.DisableConventionalLocalRouting();
                opts.Discovery.IncludeAssembly(GetType().Assembly);
                opts.Services.AddResourceSetupOnStartup();

                opts.PublishMessage<Kip848Message>().ToKafkaTopic("kip848");
                // BeginAtEarliest so a record produced before the group finishes joining is still consumed
                opts.ListenToKafkaTopic("kip848").BeginAtEarliest();
            }).StartAsync(cancellationToken: TestContext.Current.CancellationToken);

        await host.TrackActivity().IncludeExternalTransports().Timeout(60.Seconds())
            .WaitForMessageToBeReceivedAt<Kip848Message>(host)
            .SendMessageAndWaitAsync(new Kip848Message());

        // Prove the group really joined through KIP-848: the broker reports the group's type
        using var admin = new AdminClientBuilder(new AdminClientConfig
        {
            BootstrapServers = KafkaContainerFixture.ConnectionString
        }).Build();
        var description = await admin.DescribeConsumerGroupsAsync(["kip848-e2e"]);
        description.ConsumerGroupDescriptions.Single().GroupType.ShouldBe(ConsumerGroupType.Consumer);

        // GH-3454 parity: the degrade-only connection-state heuristic behaves identically under the new
        // protocol — a healthy listener rests at Unknown and never synthesizes Connected
        var snapshot = host.GetRuntime().Endpoints.CollectEndpointHealth()
            .Single(s => s.Direction == EndpointDirection.Listening && s.Uri.Scheme == "kafka");
        snapshot.ConnectionState.ShouldBe(TransportConnectionState.Unknown);
    }

    [Fact]
    public async Task static_membership_and_per_topic_overrides_work_under_kip848()
    {
        using var host = await Host.CreateDefaultBuilder()
            .UseWolverine(opts =>
            {
                opts.ServiceName = "kip848-guarded";
                opts.UseKafka(KafkaContainerFixture.ConnectionString)
                    // Static membership IS supported under KIP-848
                    .UseStaticMembership("kip848-node-1")
                    .AutoProvision();
                opts.Policies.DisableConventionalLocalRouting();
                opts.Discovery.IncludeAssembly(GetType().Assembly);
                opts.Services.AddResourceSetupOnStartup();

                opts.PublishMessage<Kip848GuardedMessage>().ToKafkaTopic("kip848-guarded");
                opts.ListenToKafkaTopic("kip848-guarded").BeginAtEarliest();

                // A per-topic ConfigureConsumer replaces the consumer config wholesale but inherits the
                // transport's GroupId, so this topic joins the same group over the same protocol
                opts.ListenToKafkaTopic("kip848-override")
                    .ConfigureConsumer(c => c.SessionTimeout = TimeSpan.FromSeconds(15))
                    .BeginAtEarliest();
            }).StartAsync(cancellationToken: TestContext.Current.CancellationToken);

        await host.TrackActivity().IncludeExternalTransports().Timeout(60.Seconds())
            .WaitForMessageToBeReceivedAt<Kip848GuardedMessage>(host)
            .SendMessageAndWaitAsync(new Kip848GuardedMessage());

        var transport = host.GetRuntime().Options.Transports.GetOrCreate<KafkaTransport>();
        transport.ConsumerConfig.GroupInstanceId.ShouldBe("kip848-node-1");

        var overrideTopic = transport.Topics["kip848-override"];
        overrideTopic.ConsumerConfig.ShouldNotBeNull();
        overrideTopic.ConsumerConfig!.SessionTimeout.ShouldBe(TimeSpan.FromSeconds(15));
    }
}

public record Kip848Message;

public record Kip848GuardedMessage;

public static class Kip848MessageHandler
{
    public static void Handle(Kip848Message message)
    {
    }

    public static void Handle(Kip848GuardedMessage message)
    {
    }
}
