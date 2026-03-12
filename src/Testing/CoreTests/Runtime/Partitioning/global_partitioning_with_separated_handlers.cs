using JasperFx.CodeGeneration;
using JasperFx.Core;
using Microsoft.Extensions.Hosting;
using Wolverine.Runtime.Partitioning;
using Wolverine.Tracking;
using Xunit;

namespace CoreTests.Runtime.Partitioning;

public class global_partitioning_with_separated_handlers
{
    [Fact]
    public async Task multiple_global_partitions_with_separated_handlers_for_same_message()
    {
        // Track which handlers have been invoked
        PartitionHandlerOne.Reset();
        PartitionHandlerTwo.Reset();
        CascadeHandlerOne.Reset();
        CascadeHandlerTwo.Reset();

        using var host = await Host.CreateDefaultBuilder()
            .UseWolverine(opts =>
            {
                opts.Discovery.DisableConventionalDiscovery()
                    .IncludeType(typeof(PartitionHandlerOne))
                    .IncludeType(typeof(PartitionHandlerTwo))
                    .IncludeType(typeof(CascadeHandlerOne))
                    .IncludeType(typeof(CascadeHandlerTwo));

                opts.CodeGeneration.TypeLoadMode = TypeLoadMode.Auto;
                opts.MultipleHandlerBehavior = MultipleHandlerBehavior.Separated;

                // First global partition
                opts.MessagePartitioning.GlobalPartitioned(gp =>
                {
                    var external = new LocalPartitionedMessageTopology(opts, "partition-a", 3);
                    gp.SetExternalTopology(external, "partition-a");
                    gp.Message<PartitionedCommand>();
                    gp.Message<CascadedFromPartition>();
                });

                // Second global partition for the same messages
                opts.MessagePartitioning.GlobalPartitioned(gp =>
                {
                    var external = new LocalPartitionedMessageTopology(opts, "partition-b", 3);
                    gp.SetExternalTopology(external, "partition-b");
                    gp.Message<PartitionedCommand>();
                    gp.Message<CascadedFromPartition>();
                });

                opts.MessagePartitioning
                    .ByMessage<PartitionedCommand>(m => m.GroupId)
                    .ByMessage<CascadedFromPartition>(m => m.GroupId);
            }).StartAsync();

        var tracked = await host.SendMessageAndWaitAsync(
            new PartitionedCommand("group-1", "test-payload"),
            timeoutInMilliseconds: 15000);

        // Both handlers for PartitionedCommand should have been invoked
        PartitionHandlerOne.Handled.ShouldBeTrue(
            "PartitionHandlerOne should have handled PartitionedCommand");
        PartitionHandlerTwo.Handled.ShouldBeTrue(
            "PartitionHandlerTwo should have handled PartitionedCommand");

        // PartitionHandlerOne returns a CascadedFromPartition message.
        // Both cascade handlers should have been invoked.
        CascadeHandlerOne.Handled.ShouldBeTrue(
            "CascadeHandlerOne should have handled CascadedFromPartition");
        CascadeHandlerTwo.Handled.ShouldBeTrue(
            "CascadeHandlerTwo should have handled CascadedFromPartition");

        // Verify the cascaded message carried the correct payload
        CascadeHandlerOne.LastPayload.ShouldBe("test-payload");
        CascadeHandlerTwo.LastPayload.ShouldBe("test-payload");
    }
}

// --- Message types ---

public record PartitionedCommand(string GroupId, string Payload);
public record CascadedFromPartition(string GroupId, string Payload);

// --- Handlers: two separate handlers for PartitionedCommand ---

public static class PartitionHandlerOne
{
    private static bool _handled;
    private static readonly object _lock = new();

    public static bool Handled
    {
        get { lock (_lock) return _handled; }
    }

    public static void Reset()
    {
        lock (_lock) _handled = false;
    }

    // Returns a cascaded message
    public static CascadedFromPartition Handle(PartitionedCommand command)
    {
        lock (_lock) _handled = true;
        return new CascadedFromPartition(command.GroupId, command.Payload);
    }
}

public static class PartitionHandlerTwo
{
    private static bool _handled;
    private static readonly object _lock = new();

    public static bool Handled
    {
        get { lock (_lock) return _handled; }
    }

    public static void Reset()
    {
        lock (_lock) _handled = false;
    }

    public static void Handle(PartitionedCommand command)
    {
        lock (_lock) _handled = true;
    }
}

// --- Handlers: two separate handlers for CascadedFromPartition ---

public static class CascadeHandlerOne
{
    private static bool _handled;
    private static string? _lastPayload;
    private static readonly object _lock = new();

    public static bool Handled
    {
        get { lock (_lock) return _handled; }
    }

    public static string? LastPayload
    {
        get { lock (_lock) return _lastPayload; }
    }

    public static void Reset()
    {
        lock (_lock)
        {
            _handled = false;
            _lastPayload = null;
        }
    }

    public static void Handle(CascadedFromPartition message)
    {
        lock (_lock)
        {
            _handled = true;
            _lastPayload = message.Payload;
        }
    }
}

public static class CascadeHandlerTwo
{
    private static bool _handled;
    private static string? _lastPayload;
    private static readonly object _lock = new();

    public static bool Handled
    {
        get { lock (_lock) return _handled; }
    }

    public static string? LastPayload
    {
        get { lock (_lock) return _lastPayload; }
    }

    public static void Reset()
    {
        lock (_lock)
        {
            _handled = false;
            _lastPayload = null;
        }
    }

    public static void Handle(CascadedFromPartition message)
    {
        lock (_lock)
        {
            _handled = true;
            _lastPayload = message.Payload;
        }
    }
}
