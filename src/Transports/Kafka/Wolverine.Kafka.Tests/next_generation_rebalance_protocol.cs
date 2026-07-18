using Confluent.Kafka;
using Confluent.Kafka.Admin;
using JasperFx.Core;
using JasperFx.Resources;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Shouldly;
using Wolverine.Configuration;
using Wolverine.Kafka.Internals;
using Wolverine.Tracking;
using Wolverine.Transports;

namespace Wolverine.Kafka.Tests;

// GH-3473: the KIP-848 next-generation consumer rebalance protocol (group.protocol=consumer).
// The config-surface and guard tests need no broker. The end-to-end tests require the docker-compose
// Kafka broker, which is a Kafka 4.0+ image (confluentinc/confluent-local:8.0.x) where the consumer
// protocol is GA and enabled by default — `docker compose up -d kafka`.
public class next_generation_rebalance_protocol
{
    // ---- configuration surface ----

    [Fact]
    public void transport_expression_sets_group_protocol_consumer()
    {
        var transport = new KafkaTransport();
        new KafkaTransportExpression(transport, new WolverineOptions()).UseNextGenerationRebalanceProtocol();

        transport.ConsumerConfig.GroupProtocol.ShouldBe(GroupProtocol.Consumer);
    }

    [Fact]
    public void listener_expression_sets_group_protocol_consumer()
    {
        var transport = new KafkaTransport();
        var topic = transport.Topics["orders"];
        var config = new KafkaListenerConfiguration(topic);
        config.UseNextGenerationRebalanceProtocol();
        ((IDelayedEndpointConfiguration)config).Apply();

        topic.ConsumerConfig.ShouldNotBeNull();
        topic.ConsumerConfig!.GroupProtocol.ShouldBe(GroupProtocol.Consumer);
    }

    // ---- bootstrap guard: librdkafka rejects the classic-protocol client-side settings outright when
    // group.protocol=consumer, so Wolverine clears them with a logged warning instead of letting every
    // listener fail at consumer creation ----

    [Fact]
    public void guard_clears_conflicting_client_side_settings_and_warns()
    {
        var logger = new RecordingLogger();
        var config = new ConsumerConfig
        {
            GroupProtocol = GroupProtocol.Consumer,
            PartitionAssignmentStrategy = PartitionAssignmentStrategy.CooperativeSticky,
            SessionTimeoutMs = 30000,
            HeartbeatIntervalMs = 3000,
            GroupProtocolType = "consumer"
        };

        KafkaGroupProtocolGuard.Sanitize(config, "transport", logger);

        config.PartitionAssignmentStrategy.ShouldBeNull();
        config.SessionTimeoutMs.ShouldBeNull();
        config.HeartbeatIntervalMs.ShouldBeNull();
        config.GroupProtocolType.ShouldBeNull();

        logger.Warnings.Count.ShouldBe(4);
        logger.Warnings.ShouldContain(w => w.Contains("partition.assignment.strategy"));
        logger.Warnings.ShouldContain(w => w.Contains("session.timeout.ms"));
        logger.Warnings.ShouldContain(w => w.Contains("heartbeat.interval.ms"));
        logger.Warnings.ShouldContain(w => w.Contains("group.protocol.type"));
    }

    [Fact]
    public void guard_leaves_static_membership_alone()
    {
        // group.instance.id IS supported under KIP-848 — never strip it
        var logger = new RecordingLogger();
        var config = new ConsumerConfig
        {
            GroupProtocol = GroupProtocol.Consumer,
            GroupInstanceId = "node-1"
        };

        KafkaGroupProtocolGuard.Sanitize(config, "transport", logger);

        config.GroupInstanceId.ShouldBe("node-1");
        logger.Warnings.ShouldBeEmpty();
    }

    [Fact]
    public void guard_is_a_no_op_for_the_classic_protocol()
    {
        var logger = new RecordingLogger();
        var config = new ConsumerConfig
        {
            PartitionAssignmentStrategy = PartitionAssignmentStrategy.CooperativeSticky,
            SessionTimeoutMs = 30000
        };

        KafkaGroupProtocolGuard.Sanitize(config, "transport", logger);

        config.PartitionAssignmentStrategy.ShouldBe(PartitionAssignmentStrategy.CooperativeSticky);
        config.SessionTimeoutMs.ShouldBe(30000);
        logger.Warnings.ShouldBeEmpty();
    }

    [Fact]
    public void guard_leaves_unrelated_settings_alone()
    {
        var logger = new RecordingLogger();
        var config = new ConsumerConfig
        {
            GroupProtocol = GroupProtocol.Consumer,
            AutoOffsetReset = AutoOffsetReset.Earliest,
            EnableAutoCommit = true
        };

        KafkaGroupProtocolGuard.Sanitize(config, "transport", logger);

        config.AutoOffsetReset.ShouldBe(AutoOffsetReset.Earliest);
        config.EnableAutoCommit.ShouldBe(true);
        logger.Warnings.ShouldBeEmpty();
    }

    [Fact]
    public void sanitized_config_passes_librdkafka_validation()
    {
        // The empirical anchor for the guard: this exact combination throws InvalidOperationException
        // from ConsumerBuilder.Build() ("`partition.assignment.strategy` is not supported for
        // `group.protocol=consumer`") before sanitizing. No broker connection is required —
        // librdkafka validates configuration at client creation.
        var config = new ConsumerConfig
        {
            BootstrapServers = KafkaContainerFixture.ConnectionString,
            GroupId = "kip848-validation",
            GroupProtocol = GroupProtocol.Consumer,
            PartitionAssignmentStrategy = PartitionAssignmentStrategy.CooperativeSticky,
            SessionTimeoutMs = 30000,
            HeartbeatIntervalMs = 3000
        };

        Should.Throw<InvalidOperationException>(() => new ConsumerBuilder<string, byte[]>(config).Build());

        KafkaGroupProtocolGuard.Sanitize(config, "test", new RecordingLogger());

        using var consumer = new ConsumerBuilder<string, byte[]>(config).Build();
        consumer.ShouldNotBeNull();
    }

    // ---- end to end against the docker-compose Kafka 4.x broker ----

    [Fact]
    public async Task end_to_end_round_trip_under_the_next_generation_protocol()
    {
        using var host = await Host.CreateDefaultBuilder()
            .UseWolverine(opts =>
            {
                opts.ServiceName = "kip848-e2e";
                opts.UseKafka(KafkaContainerFixture.ConnectionString)
                    .UseNextGenerationRebalanceProtocol()
                    .AutoProvision();
                opts.Policies.DisableConventionalLocalRouting();
                opts.Discovery.IncludeAssembly(GetType().Assembly);
                opts.Services.AddResourceSetupOnStartup();

                opts.PublishMessage<Kip848Message>().ToKafkaTopic("kip848");
                // BeginAtEarliest so a record produced before the group finishes joining is still consumed
                opts.ListenToKafkaTopic("kip848").BeginAtEarliest();
            }).StartAsync();

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
    public async Task conflicting_client_side_settings_are_cleared_at_bootstrap_and_the_listener_still_works()
    {
        // Without the bootstrap guard this host would die at listener creation with librdkafka's
        // "`partition.assignment.strategy` is not supported for `group.protocol=consumer`"
        using var host = await Host.CreateDefaultBuilder()
            .UseWolverine(opts =>
            {
                opts.ServiceName = "kip848-guarded";
                opts.UseKafka(KafkaContainerFixture.ConnectionString)
                    .UseCooperativeStickyAssignment()
                    .ConfigureConsumers(c =>
                    {
                        c.SessionTimeoutMs = 20000;
                        c.HeartbeatIntervalMs = 2000;
                    })
                    .UseNextGenerationRebalanceProtocol()
                    // Static membership must survive the guard — it IS supported under KIP-848
                    .UseStaticMembership("kip848-node-1")
                    .AutoProvision();
                opts.Policies.DisableConventionalLocalRouting();
                opts.Discovery.IncludeAssembly(GetType().Assembly);
                opts.Services.AddResourceSetupOnStartup();

                opts.PublishMessage<Kip848GuardedMessage>().ToKafkaTopic("kip848-guarded");
                opts.ListenToKafkaTopic("kip848-guarded").BeginAtEarliest();

                // A per-topic ConfigureConsumer replaces the consumer config wholesale but inherits the
                // transport's GroupId — so the transport's group.protocol must flow down with it (and be
                // sanitized), or this topic would join the same group over the classic protocol
                opts.ListenToKafkaTopic("kip848-override")
                    .ConfigureConsumer(c => c.SessionTimeoutMs = 15000)
                    .BeginAtEarliest();
            }).StartAsync();

        await host.TrackActivity().IncludeExternalTransports().Timeout(60.Seconds())
            .WaitForMessageToBeReceivedAt<Kip848GuardedMessage>(host)
            .SendMessageAndWaitAsync(new Kip848GuardedMessage());

        var transport = host.GetRuntime().Options.Transports.GetOrCreate<KafkaTransport>();
        transport.ConsumerConfig.GroupProtocol.ShouldBe(GroupProtocol.Consumer);
        transport.ConsumerConfig.PartitionAssignmentStrategy.ShouldBeNull();
        transport.ConsumerConfig.SessionTimeoutMs.ShouldBeNull();
        transport.ConsumerConfig.HeartbeatIntervalMs.ShouldBeNull();
        transport.ConsumerConfig.GroupInstanceId.ShouldBe("kip848-node-1");

        // The per-topic override inherited the transport's group.protocol and was sanitized in turn —
        // the fact the host started at all proves librdkafka accepted the cleaned config
        var overrideTopic = transport.Topics["kip848-override"];
        overrideTopic.ConsumerConfig.ShouldNotBeNull();
        overrideTopic.ConsumerConfig!.GroupProtocol.ShouldBe(GroupProtocol.Consumer);
        overrideTopic.ConsumerConfig.SessionTimeoutMs.ShouldBeNull();
    }

    private class RecordingLogger : ILogger
    {
        public List<string> Warnings { get; } = new();

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            if (logLevel == LogLevel.Warning)
            {
                Warnings.Add(formatter(state, exception));
            }
        }
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
