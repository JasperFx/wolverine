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

                // First global partition
                opts.MessagePartitioning.GlobalPartitioned(gp =>
                {
                    var external = new LocalPartitionedMessageTopology(opts, "partition-a", 3);
                    gp.SetExternalTopology(external, "partition-a");
                    gp.Message<PartitionedCommand>();
                    gp.Message<CascadedFromPartition>();

                    // GH-5916. With two topologies publishing the same message types, each one runs only the
                    // handler types it declares
                    gp.HandledBy(typeof(PartitionHandlerOne), typeof(CascadeHandlerOne));
                });

                // Second global partition for the same messages
                opts.MessagePartitioning.GlobalPartitioned(gp =>
                {
                    var external = new LocalPartitionedMessageTopology(opts, "partition-b", 3);
                    gp.SetExternalTopology(external, "partition-b");
                    gp.Message<PartitionedCommand>();
                    gp.Message<CascadedFromPartition>();
                    gp.HandledBy(typeof(PartitionHandlerTwo), typeof(CascadeHandlerTwo));
                });

                opts.MessagePartitioning
                    .ByMessage<PartitionedCommand>(m => m.GroupId)
                    .ByMessage<CascadedFromPartition>(m => m.GroupId);
            }).StartAsync(cancellationToken: TestContext.Current.CancellationToken);

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

        // GH-5916. Each handler ran exactly once, not once per topology
        PartitionHandlerOne.Count.ShouldBe(1);
        PartitionHandlerTwo.Count.ShouldBe(1);
        CascadeHandlerOne.Count.ShouldBe(1);
        CascadeHandlerTwo.Count.ShouldBe(1);

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
    private static int _count;

    public static int Count
    {
        get { lock (_lock) return _count; }
    }
    private static readonly object _lock = new();

    public static bool Handled
    {
        get { lock (_lock) return _handled; }
    }

    public static void Reset()
    {
        lock (_lock)
        {
            _handled = false;
            _count = 0;
        }
    }

    // Returns a cascaded message
    public static CascadedFromPartition Handle(PartitionedCommand command)
    {
        lock (_lock)
        {
            _handled = true;
            _count++;
        }
        return new CascadedFromPartition(command.GroupId, command.Payload);
    }
}

public static class PartitionHandlerTwo
{
    private static bool _handled;
    private static int _count;

    public static int Count
    {
        get { lock (_lock) return _count; }
    }
    private static readonly object _lock = new();

    public static bool Handled
    {
        get { lock (_lock) return _handled; }
    }

    public static void Reset()
    {
        lock (_lock)
        {
            _handled = false;
            _count = 0;
        }
    }

    public static void Handle(PartitionedCommand command)
    {
        lock (_lock)
        {
            _handled = true;
            _count++;
        }
    }
}

// --- Handlers: two separate handlers for CascadedFromPartition ---

public static class CascadeHandlerOne
{
    private static bool _handled;
    private static int _count;

    public static int Count
    {
        get { lock (_lock) return _count; }
    }
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
            _count = 0;
            _lastPayload = null;
        }
    }

    public static void Handle(CascadedFromPartition message)
    {
        lock (_lock)
        {
            _handled = true;
            _count++;
            _lastPayload = message.Payload;
        }
    }
}

public static class CascadeHandlerTwo
{
    private static bool _handled;
    private static int _count;

    public static int Count
    {
        get { lock (_lock) return _count; }
    }
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
            _count = 0;
            _lastPayload = null;
        }
    }

    public static void Handle(CascadedFromPartition message)
    {
        lock (_lock)
        {
            _handled = true;
            _count++;
            _lastPayload = message.Payload;
        }
    }
}
