using JasperFx.CodeGeneration;
using JasperFx.Core;
using Microsoft.Extensions.Hosting;
using Wolverine.Attributes;
using Wolverine.Runtime.Partitioning;
using Wolverine.Tracking;
using Xunit;

namespace CoreTests.Runtime.Partitioning;

/// <summary>
/// Reproduces https://github.com/JasperFx/wolverine/issues/2303
/// Sticky handlers in modular monolith re-execute multiple times with global partitioning enabled
/// </summary>
public class sticky_handlers_with_global_partitioning
{
    [Fact]
    public async Task sticky_handler_should_execute_exactly_once_per_handler_with_global_partitioning()
    {
        // Reset counters
        StickyPartitionHandlerA.Reset();
        StickyPartitionHandlerB.Reset();

        using var host = await Host.CreateDefaultBuilder()
            .UseWolverine(opts =>
            {
                opts.Discovery.DisableConventionalDiscovery()
                    .IncludeType(typeof(StickyPartitionHandlerA))
                    .IncludeType(typeof(StickyPartitionHandlerB));

                opts.CodeGeneration.TypeLoadMode = TypeLoadMode.Auto;
                opts.Durability.Mode = DurabilityMode.Solo;

                // Configure sticky handlers on local queues (like the modular monolith pattern)
                opts.LocalQueue("handler-a").AddStickyHandler(typeof(StickyPartitionHandlerA));
                opts.LocalQueue("handler-b").AddStickyHandler(typeof(StickyPartitionHandlerB));

                // Enable global partitioning with local topology (simulates Kafka sharding)
                opts.MessagePartitioning.GlobalPartitioned(gp =>
                {
                    var external = new LocalPartitionedMessageTopology(opts, "sticky-partition", 2);
                    gp.SetExternalTopology(external, "sticky-partition");
                    gp.Message<StickyPartitionedMessage>();
                });

                opts.MessagePartitioning
                    .ByMessage<StickyPartitionedMessage>(m => m.Id);

                // This is the key: propagate group ID to partition key (as in the bug report)
                opts.Policies.PropagateGroupIdToPartitionKey();
            }).StartAsync();

        var message = new StickyPartitionedMessage("group-1", "test-payload");
        var session = await host.SendMessageAndWaitAsync(message, timeoutInMilliseconds: 15000);

        // Each handler should execute exactly once
        StickyPartitionHandlerA.ExecutionCount.ShouldBe(1,
            $"Handler A should execute exactly once, but executed {StickyPartitionHandlerA.ExecutionCount} times");
        StickyPartitionHandlerB.ExecutionCount.ShouldBe(1,
            $"Handler B should execute exactly once, but executed {StickyPartitionHandlerB.ExecutionCount} times");
    }

    [Fact]
    public async Task sticky_handler_should_execute_exactly_once_with_multiple_messages()
    {
        StickyPartitionHandlerA.Reset();
        StickyPartitionHandlerB.Reset();

        using var host = await Host.CreateDefaultBuilder()
            .UseWolverine(opts =>
            {
                opts.Discovery.DisableConventionalDiscovery()
                    .IncludeType(typeof(StickyPartitionHandlerA))
                    .IncludeType(typeof(StickyPartitionHandlerB));

                opts.CodeGeneration.TypeLoadMode = TypeLoadMode.Auto;
                opts.Durability.Mode = DurabilityMode.Solo;

                opts.LocalQueue("handler-a").AddStickyHandler(typeof(StickyPartitionHandlerA));
                opts.LocalQueue("handler-b").AddStickyHandler(typeof(StickyPartitionHandlerB));

                opts.MessagePartitioning.GlobalPartitioned(gp =>
                {
                    var external = new LocalPartitionedMessageTopology(opts, "sticky-partition-multi", 2);
                    gp.SetExternalTopology(external, "sticky-partition-multi");
                    gp.Message<StickyPartitionedMessage>();
                });

                opts.MessagePartitioning
                    .ByMessage<StickyPartitionedMessage>(m => m.Id);

                opts.Policies.PropagateGroupIdToPartitionKey();
            }).StartAsync();

        // Send 3 messages with the same group ID
        for (int i = 0; i < 3; i++)
        {
            await host.SendMessageAndWaitAsync(
                new StickyPartitionedMessage("group-1", $"payload-{i}"),
                timeoutInMilliseconds: 15000);
        }

        // Each handler should execute exactly 3 times (once per message)
        StickyPartitionHandlerA.ExecutionCount.ShouldBe(3,
            $"Handler A should execute 3 times, but executed {StickyPartitionHandlerA.ExecutionCount} times");
        StickyPartitionHandlerB.ExecutionCount.ShouldBe(3,
            $"Handler B should execute 3 times, but executed {StickyPartitionHandlerB.ExecutionCount} times");
    }
}

// --- Message type ---
public record StickyPartitionedMessage(string Id, string Payload);

// --- Sticky handlers that count executions ---
public static class StickyPartitionHandlerA
{
    private static int _executionCount;
    private static readonly object _lock = new();

    public static int ExecutionCount
    {
        get { lock (_lock) return _executionCount; }
    }

    public static void Reset()
    {
        lock (_lock) _executionCount = 0;
    }

    public static void Handle(StickyPartitionedMessage message)
    {
        lock (_lock) _executionCount++;
    }
}

public static class StickyPartitionHandlerB
{
    private static int _executionCount;
    private static readonly object _lock = new();

    public static int ExecutionCount
    {
        get { lock (_lock) return _executionCount; }
    }

    public static void Reset()
    {
        lock (_lock) _executionCount = 0;
    }

    public static void Handle(StickyPartitionedMessage message)
    {
        lock (_lock) _executionCount++;
    }
}
