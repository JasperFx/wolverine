using JasperFx.Core;
using JasperFx.Resources;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Shouldly;
using Wolverine.Tracking;
using Xunit;
using Xunit.Abstractions;

namespace Wolverine.Kafka.Tests;

/// <summary>
/// Reproduces https://github.com/JasperFx/wolverine/issues/2303
/// Sticky handlers in modular monolith re-execute multiple times with global partitioning enabled.
///
/// The scenario:
/// - A message type is published to Kafka via global partitioning (sharded topics)
/// - The same message type also routes to local queues via sticky handlers
/// - PropagateGroupIdToPartitionKey is enabled
/// - Expected: each sticky handler executes exactly once per message
/// - Bug: handlers execute multiple times
/// </summary>
public class sticky_handlers_with_global_partitioning
{
    private readonly ITestOutputHelper _output;

    public sticky_handlers_with_global_partitioning(ITestOutputHelper output)
    {
        _output = output;
    }

    [Fact]
    public async Task each_sticky_handler_executes_exactly_once_per_message()
    {
        KafkaStickyHandlerA.Reset();
        KafkaStickyHandlerB.Reset();

        using var host = await Host.CreateDefaultBuilder()
            .UseWolverine(opts =>
            {
                opts.Durability.Mode = DurabilityMode.Solo;
                opts.UseKafka(KafkaContainerFixture.ConnectionString).AutoProvision();

                opts.Discovery.DisableConventionalDiscovery()
                    .IncludeType(typeof(KafkaStickyHandlerA))
                    .IncludeType(typeof(KafkaStickyHandlerB));

                // Sticky handlers on local queues (modular monolith pattern)
                opts.LocalQueue("kafka-sticky-a")
                    .AddStickyHandler(typeof(KafkaStickyHandlerA));
                opts.LocalQueue("kafka-sticky-b")
                    .AddStickyHandler(typeof(KafkaStickyHandlerB));

                // Global partitioning via Kafka sharded topics
                opts.MessagePartitioning.GlobalPartitioned(topology =>
                {
                    topology.UseShardedKafkaTopics("sticky-test-2303", 2);
                    topology.Message<KafkaStickyCommand>();
                });

                opts.MessagePartitioning
                    .ByMessage<KafkaStickyCommand>(m => m.GroupId);

                // Key ingredient from the bug report
                opts.Policies.PropagateGroupIdToPartitionKey();

                opts.Services.AddResourceSetupOnStartup();
            }).StartAsync();

        // Publish a single message
        var message = new KafkaStickyCommand("group-1", "payload-1");
        var session = await host
            .TrackActivity()
            .IncludeExternalTransports()
            .Timeout(30.Seconds())
            .SendMessageAndWaitAsync(message);

        // Log what happened for debugging
        foreach (var envelope in session.Executed.Envelopes())
        {
            _output.WriteLine(
                $"Executed: {envelope.Message?.GetType().Name} at {envelope.Destination}");
        }

        // Each handler should execute exactly once
        KafkaStickyHandlerA.ExecutionCount.ShouldBe(1,
            $"Handler A should execute exactly once, but executed {KafkaStickyHandlerA.ExecutionCount} times. " +
            $"Payloads seen: [{string.Join(", ", KafkaStickyHandlerA.PayloadsSeen)}]");
        KafkaStickyHandlerB.ExecutionCount.ShouldBe(1,
            $"Handler B should execute exactly once, but executed {KafkaStickyHandlerB.ExecutionCount} times. " +
            $"Payloads seen: [{string.Join(", ", KafkaStickyHandlerB.PayloadsSeen)}]");
    }

    /// <summary>
    /// This test reproduces the exact pattern from issue #2303:
    /// - Message is published to Kafka topic via Publish()
    /// - ALSO published to local queues with sticky handlers via Publish()
    /// - ALSO routed via GlobalPartitioned which fans out to the same sticky queues
    /// This should NOT cause duplicate handler execution.
    /// </summary>
    [Fact]
    public async Task dual_publish_to_kafka_and_local_sticky_should_not_double_execute()
    {
        KafkaStickyHandlerA.Reset();
        KafkaStickyHandlerB.Reset();

        using var host = await Host.CreateDefaultBuilder()
            .UseWolverine(opts =>
            {
                opts.Durability.Mode = DurabilityMode.Solo;
                opts.UseKafka(KafkaContainerFixture.ConnectionString).AutoProvision();

                opts.Discovery.DisableConventionalDiscovery()
                    .IncludeType(typeof(KafkaStickyHandlerA))
                    .IncludeType(typeof(KafkaStickyHandlerB));

                // Replicate the issue reporter's pattern:
                // 1. Publish to Kafka topic
                opts.PublishMessage<KafkaStickyCommand>()
                    .ToKafkaTopic("sticky-dual-2303");

                // 2. Also publish to local queues with sticky handlers
                opts.Publish(c => c.Message<KafkaStickyCommand>()
                    .ToLocalQueue("kafka-sticky-a-dual")
                    .AddStickyHandler(typeof(KafkaStickyHandlerA)));
                opts.Publish(c => c.Message<KafkaStickyCommand>()
                    .ToLocalQueue("kafka-sticky-b-dual")
                    .AddStickyHandler(typeof(KafkaStickyHandlerB)));

                // 3. Global partitioning for the same message type
                opts.MessagePartitioning.GlobalPartitioned(topology =>
                {
                    topology.UseShardedKafkaTopics("sticky-dual-gp-2303", 2);
                    topology.Message<KafkaStickyCommand>();
                });

                opts.MessagePartitioning
                    .ByMessage<KafkaStickyCommand>(m => m.GroupId);

                opts.Policies.PropagateGroupIdToPartitionKey();

                opts.Services.AddResourceSetupOnStartup();
            }).StartAsync();

        var message = new KafkaStickyCommand("group-1", "dual-payload");

        // Don't use tracked session since the duplicate execution causes tracking to hang.
        // Instead just publish and wait.
        var bus = host.Services.GetRequiredService<IMessageBus>();
        await bus.PublishAsync(message);

        // Wait for handlers to process
        await Task.Delay(10.Seconds());

        _output.WriteLine($"Handler A executed {KafkaStickyHandlerA.ExecutionCount} times");
        _output.WriteLine($"Handler B executed {KafkaStickyHandlerB.ExecutionCount} times");
        _output.WriteLine($"Handler A payloads: [{string.Join(", ", KafkaStickyHandlerA.PayloadsSeen)}]");
        _output.WriteLine($"Handler B payloads: [{string.Join(", ", KafkaStickyHandlerB.PayloadsSeen)}]");

        // Each handler should execute exactly once, NOT twice
        // BUG: With dual routing (Kafka publish + local sticky + global partitioning),
        // messages get routed through both the GlobalPartitionedRoute AND the explicit
        // local queue routes, causing duplicate execution.
        KafkaStickyHandlerA.ExecutionCount.ShouldBe(1,
            $"Handler A should execute exactly once, but executed {KafkaStickyHandlerA.ExecutionCount} times. " +
            $"This is the bug from issue #2303 - dual routing causes duplicate execution. " +
            $"Payloads: [{string.Join(", ", KafkaStickyHandlerA.PayloadsSeen)}]");
        KafkaStickyHandlerB.ExecutionCount.ShouldBe(1,
            $"Handler B should execute exactly once, but executed {KafkaStickyHandlerB.ExecutionCount} times. " +
            $"This is the bug from issue #2303 - dual routing causes duplicate execution. " +
            $"Payloads: [{string.Join(", ", KafkaStickyHandlerB.PayloadsSeen)}]");
    }

    [Fact]
    public async Task multiple_messages_each_handled_exactly_once_per_handler()
    {
        KafkaStickyHandlerA.Reset();
        KafkaStickyHandlerB.Reset();

        using var host = await Host.CreateDefaultBuilder()
            .UseWolverine(opts =>
            {
                opts.Durability.Mode = DurabilityMode.Solo;
                opts.UseKafka(KafkaContainerFixture.ConnectionString).AutoProvision();

                opts.Discovery.DisableConventionalDiscovery()
                    .IncludeType(typeof(KafkaStickyHandlerA))
                    .IncludeType(typeof(KafkaStickyHandlerB));

                opts.LocalQueue("kafka-sticky-a2")
                    .AddStickyHandler(typeof(KafkaStickyHandlerA));
                opts.LocalQueue("kafka-sticky-b2")
                    .AddStickyHandler(typeof(KafkaStickyHandlerB));

                opts.MessagePartitioning.GlobalPartitioned(topology =>
                {
                    topology.UseShardedKafkaTopics("sticky-test-2303-multi", 2);
                    topology.Message<KafkaStickyCommand>();
                });

                opts.MessagePartitioning
                    .ByMessage<KafkaStickyCommand>(m => m.GroupId);

                opts.Policies.PropagateGroupIdToPartitionKey();

                opts.Services.AddResourceSetupOnStartup();
            }).StartAsync();

        // Send 3 messages
        for (int i = 0; i < 3; i++)
        {
            await host
                .TrackActivity()
                .IncludeExternalTransports()
                .Timeout(30.Seconds())
                .SendMessageAndWaitAsync(new KafkaStickyCommand($"group-{i}", $"payload-{i}"));
        }

        _output.WriteLine($"Handler A executed {KafkaStickyHandlerA.ExecutionCount} times");
        _output.WriteLine($"Handler B executed {KafkaStickyHandlerB.ExecutionCount} times");

        // Each handler should execute exactly 3 times (once per message)
        KafkaStickyHandlerA.ExecutionCount.ShouldBe(3,
            $"Handler A should execute 3 times, but executed {KafkaStickyHandlerA.ExecutionCount} times");
        KafkaStickyHandlerB.ExecutionCount.ShouldBe(3,
            $"Handler B should execute 3 times, but executed {KafkaStickyHandlerB.ExecutionCount} times");
    }
}

// --- Message type ---
public record KafkaStickyCommand(string GroupId, string Payload);

// --- Sticky handlers that count executions ---
public static class KafkaStickyHandlerA
{
    private static int _executionCount;
    private static readonly List<string> _payloadsSeen = new();
    private static readonly object _lock = new();

    public static int ExecutionCount
    {
        get { lock (_lock) return _executionCount; }
    }

    public static List<string> PayloadsSeen
    {
        get { lock (_lock) return new List<string>(_payloadsSeen); }
    }

    public static void Reset()
    {
        lock (_lock)
        {
            _executionCount = 0;
            _payloadsSeen.Clear();
        }
    }

    public static void Handle(KafkaStickyCommand message)
    {
        lock (_lock)
        {
            _executionCount++;
            _payloadsSeen.Add(message.Payload);
        }
    }
}

public static class KafkaStickyHandlerB
{
    private static int _executionCount;
    private static readonly List<string> _payloadsSeen = new();
    private static readonly object _lock = new();

    public static int ExecutionCount
    {
        get { lock (_lock) return _executionCount; }
    }

    public static List<string> PayloadsSeen
    {
        get { lock (_lock) return new List<string>(_payloadsSeen); }
    }

    public static void Reset()
    {
        lock (_lock)
        {
            _executionCount = 0;
            _payloadsSeen.Clear();
        }
    }

    public static void Handle(KafkaStickyCommand message)
    {
        lock (_lock)
        {
            _executionCount++;
            _payloadsSeen.Add(message.Payload);
        }
    }
}
