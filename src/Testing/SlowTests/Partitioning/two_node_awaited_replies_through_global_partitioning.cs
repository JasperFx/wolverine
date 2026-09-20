using System.Collections.Concurrent;
using IntegrationTests;
using JasperFx;
using JasperFx.Core;
using Marten;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Npgsql;
using Shouldly;
using Wolverine;
using Wolverine.Marten;
using Wolverine.RabbitMQ;
using Wolverine.Runtime;
using Wolverine.Runtime.Agents;
using Wolverine.Runtime.Partitioning;
using Wolverine.Runtime.RemoteInvocation;
using Xunit;

namespace SlowTests.Partitioning;

/// <summary>
/// Two nodes sharing one RabbitMQ topology and one Postgres message store, with partition ownership split
/// between them by agent assignment. Every request targets a group id whose slot the CALLER DOES NOT OWN,
/// which is what proves it crossed the broker and executed on the owner. The in-process counterpart is
/// <c>awaited_replies_through_global_partitioning</c> in CoreTests.
/// </summary>
[Collection("partitioning")]
public class two_node_awaited_replies_through_global_partitioning : IAsyncLifetime
{
    private const string BaseName = "twonode";
    private const int SlotCount = 5;
    private const string SchemaName = "twonode_partitioning";

    private IHost _nodeA = null!;
    private IHost _nodeB = null!;

    public async ValueTask InitializeAsync()
    {
        await using (var conn = new NpgsqlConnection(Servers.PostgresConnectionString))
        {
            await conn.OpenAsync();
            await using var cmd = conn.CreateCommand();
            cmd.CommandText = $"DROP SCHEMA IF EXISTS {SchemaName} CASCADE;";
            await cmd.ExecuteNonQueryAsync();
        }

        LedgerHandler.Reset();

        // Started sequentially so the first node creates the Marten schema without a DDL race
        _nodeA = await buildHost("node-a").StartAsync();
        _nodeB = await buildHost("node-b").StartAsync();
    }

    public async ValueTask DisposeAsync()
    {
        await _nodeA.StopAsync();
        await _nodeB.StopAsync();
        _nodeA.Dispose();
        _nodeB.Dispose();
    }

    private static IHostBuilder buildHost(string nodeName)
    {
        return Host.CreateDefaultBuilder()
            .UseWolverine(opts =>
            {
                // Balanced, NOT Solo: under Solo one node owns every exclusive listener and the local
                // shortcut answers every request in process, the path this test exists to avoid
                opts.ServiceName = "TwoNodePartitioning";
                opts.Durability.Mode = DurabilityMode.Balanced;

                opts.UseRabbitMq().AutoProvision().AutoPurgeOnStartup();

                opts.Discovery.DisableConventionalDiscovery().IncludeType(typeof(LedgerHandler));

                opts.Services.AddMarten(m =>
                {
                    m.Connection(Servers.PostgresConnectionString);
                    m.DatabaseSchemaName = SchemaName;
                    m.DisableNpgsqlLogging = true;
                }).IntegrateWithWolverine();

                opts.MessagePartitioning.ByMessage<PostEntry>(x => x.AccountId);
                opts.MessagePartitioning.ByMessage<PostBadEntry>(x => x.AccountId);
                opts.MessagePartitioning.ByMessage<NestedEntry>(x => x.AccountId);

                opts.MessagePartitioning.GlobalPartitioned(topology =>
                {
                    topology.UseShardedRabbitQueues(BaseName, SlotCount);
                    topology.Message<PostEntry>();
                    topology.Message<PostBadEntry>();
                    topology.Message<NestedEntry>();
                });

                opts.Services.AddSingleton(new NodeName(nodeName));
            });
    }

    [Fact]
    public async Task reply_returns_to_the_caller_when_another_node_owns_the_partition()
    {
        var owners = await waitForStablePartitionOwnershipAsync();

        var accountId = findAccountIdOwnedBy(_nodeB, owners);

        var reply = await _nodeA.MessageBus()
            .InvokeAsync<EntryPosted>(new PostEntry(accountId, 42),
                new DeliveryOptions { InvokeThroughRouting = true }, CancellationToken.None, 60.Seconds());

        reply.AccountId.ShouldBe(accountId);
        reply.Amount.ShouldBe(42);
        reply.HandledBy.ShouldBe("node-b",
            "The command must execute on the node that owns the partition, not on the caller");
    }

    [Fact]
    public async Task one_group_stays_sequential_on_its_owner_across_both_nodes()
    {
        var owners = await waitForStablePartitionOwnershipAsync();
        var accountId = findAccountIdOwnedBy(_nodeB, owners);

        LedgerHandler.Reset();

        var callers = new[] { _nodeA, _nodeB, _nodeA, _nodeB, _nodeA, _nodeB };
        var replies = await Task.WhenAll(callers.Select((host, i) => host.MessageBus()
            .InvokeAsync<EntryPosted>(new PostEntry(accountId, i),
                new DeliveryOptions { InvokeThroughRouting = true }, CancellationToken.None, 60.Seconds())));

        replies.Length.ShouldBe(callers.Length);
        replies.Select(x => x.HandledBy).Distinct().ShouldHaveSingleItem().ShouldBe("node-b");
        LedgerHandler.MaxConcurrency.ShouldBe(1,
            "Messages sharing a group id must never execute concurrently, even when invoked from different nodes");
    }

    [Fact]
    public async Task a_failure_on_the_owning_node_surfaces_as_a_request_reply_exception()
    {
        var owners = await waitForStablePartitionOwnershipAsync();
        var accountId = findAccountIdOwnedBy(_nodeB, owners);

        var ex = await Should.ThrowAsync<WolverineRequestReplyException>(() => _nodeA.MessageBus()
            .InvokeAsync<EntryPosted>(new PostBadEntry(accountId),
                new DeliveryOptions { InvokeThroughRouting = true }, CancellationToken.None, 60.Seconds()));

        ex.Message.ShouldContain("this entry cannot be posted");
    }

    [Fact]
    public async Task refuses_direct_lane_reentry_on_the_owning_node_for_the_same_group_id()
    {
        // Over the broker, Envelope.Destination names the external slot listener while the handler runs on
        // the companion queue's lane, so only the shared slot index can see the nested invoke re-enter it
        var owners = await waitForStablePartitionOwnershipAsync();
        var accountId = findAccountIdOwnedBy(_nodeB, owners);

        await _nodeA.MessageBus().InvokeAsync<EntryPosted>(new PostEntry(accountId, 1, accountId),
            new DeliveryOptions { InvokeThroughRouting = true }, CancellationToken.None, 120.Seconds());

        assertRefusedImmediately();
    }

    [Fact]
    public async Task refuses_direct_lane_reentry_on_the_owning_node_for_a_different_group_id_on_the_same_lane()
    {
        var owners = await waitForStablePartitionOwnershipAsync();
        var accountId = findAccountIdOwnedBy(_nodeB, owners);
        var sameLane = findSameLaneCompanion(accountId);

        sameLane.ShouldNotBe(accountId);

        await _nodeA.MessageBus().InvokeAsync<EntryPosted>(new PostEntry(accountId, 1, sameLane),
            new DeliveryOptions { InvokeThroughRouting = true }, CancellationToken.None, 120.Seconds());

        assertRefusedImmediately();
    }

    [Fact]
    public async Task allows_a_nested_invoke_onto_a_different_lane_of_the_same_queue()
    {
        var owners = await waitForStablePartitionOwnershipAsync();
        var accountId = findAccountIdOwnedBy(_nodeB, owners);
        var otherLane = findDifferentLaneOnTheSameSlot(accountId);

        laneOf(otherLane).Slot.ShouldBe(laneOf(accountId).Slot);
        laneOf(otherLane).Lane.ShouldNotBe(laneOf(accountId).Lane);

        await _nodeA.MessageBus().InvokeAsync<EntryPosted>(new PostEntry(accountId, 1, otherLane),
            new DeliveryOptions { InvokeThroughRouting = true }, CancellationToken.None, 120.Seconds());

        LedgerHandler.NestedOutcome.ShouldBe("succeeded",
            $"A nested invoke onto a different lane must still work. Got: {LedgerHandler.NestedOutcome} / {LedgerHandler.NestedMessage}");
    }

    private static void assertRefusedImmediately()
    {
        LedgerHandler.NestedOutcome.ShouldBe(nameof(InvalidOperationException),
            $"The nested invoke should have been refused outright. Got: {LedgerHandler.NestedOutcome} / {LedgerHandler.NestedMessage}");
        LedgerHandler.NestedMessage.ShouldNotBeNull().ShouldContain("same partitioned lane");

        // A deadlock that resolves by timing out also throws, just 30 seconds later
        LedgerHandler.NestedElapsed.ShouldBeLessThan(5.Seconds(),
            "The refusal must be immediate, not the nested invocation timing out");
    }

    /// <summary>
    /// "Usable" is not "every slot claimed": an unclaimed slot is simply never chosen as a target, and waiting
    /// for all of them made the suite hostage to the slowest listener. What the tests need is no contested
    /// slot and both nodes owning something, observed twice in a row so a mid-handoff snapshot is not taken
    /// for a settled one.
    /// </summary>
    private async Task<Dictionary<int, IHost>> waitForStablePartitionOwnershipAsync()
    {
        var deadline = DateTimeOffset.UtcNow.AddSeconds(120);
        Dictionary<int, IHost>? confirming = null;

        while (DateTimeOffset.UtcNow < deadline)
        {
            var owners = new Dictionary<int, IHost>();
            var contested = false;

            for (var slot = 0; slot < SlotCount; slot++)
            {
                var claimants = new[] { _nodeA, _nodeB }.Where(host => ownsSlot(host, slot)).ToArray();

                if (claimants.Length == 1)
                {
                    owners[slot] = claimants[0];
                }
                else if (claimants.Length > 1)
                {
                    // Mid-handoff: two nodes briefly report Accepting for one slot
                    contested = true;
                }
            }

            var usable = !contested && owners.Values.Contains(_nodeA) && owners.Values.Contains(_nodeB);

            if (usable && confirming != null && sameOwnership(confirming, owners))
            {
                return owners;
            }

            confirming = usable ? owners : null;
            await Task.Delay(500.Milliseconds());
        }

        throw new TimeoutException(
            $"Partition ownership across the two nodes did not settle within 120 seconds. Current state: {describeOwnership()}");
    }

    private static bool sameOwnership(Dictionary<int, IHost> left, Dictionary<int, IHost> right)
    {
        return left.Count == right.Count
               && left.All(pair => right.TryGetValue(pair.Key, out var host) && ReferenceEquals(host, pair.Value));
    }

    private static bool ownsSlot(IHost host, int slot)
    {
        // Not Endpoints.FindListeningAgent: it reads a plain Dictionary that parallel agent starts
        // mutate. An agent is registered only after its listener started, so running means accepting.
        return host.RunningAgents().Contains(slotAgentUri(host, slot));
    }

    private static Uri slotAgentUri(IHost host, int slot)
    {
        var runtime = host.Services.GetRequiredService<IWolverineRuntime>();
        var topology = runtime.Options.MessagePartitioning.GlobalPartitionedTopologies.Single();
        var slotEndpoint = topology.ExternalTopology!.Slots[slot];
        return new Uri(
            $"{ExclusiveListenerFamily.SchemeName}://{slotEndpoint.Uri.Scheme}/{slotEndpoint.EndpointName}");
    }

    private string describeOwnership()
    {
        return Enumerable.Range(0, SlotCount)
            .Select(slot =>
            {
                var a = ownsSlot(_nodeA, slot) ? "A" : "";
                var b = ownsSlot(_nodeB, slot) ? "B" : "";
                return $"slot {slot}: [{a}{b}]";
            })
            .Join(", ");
    }

    private string findSameLaneCompanion(string seed)
    {
        var target = laneOf(seed);
        return candidateAccountIds().First(id => id != seed && laneOf(id) == target);
    }

    /// <summary>
    /// The slot has to match, not merely the owning node: a different slot is a different companion queue.
    /// </summary>
    private string findDifferentLaneOnTheSameSlot(string seed)
    {
        var target = laneOf(seed);
        return candidateAccountIds().First(id =>
        {
            var lane = laneOf(id);
            return lane.Slot == target.Slot && lane.Lane != target.Lane;
        });
    }

    private (int Slot, int Lane) laneOf(string accountId)
    {
        var rules = _nodeA.Services.GetRequiredService<IWolverineRuntime>().Options.MessagePartitioning;
        var slot = new Envelope(new PostEntry(accountId, 0)).SlotForSending(SlotCount, rules);

        // The lane count is the COMPANION queue's, where execution actually happens
        var topology = _nodeA.Services.GetRequiredService<IWolverineRuntime>()
            .Options.MessagePartitioning.GlobalPartitionedTopologies.Single();
        var lanes = (int)topology.LocalTopology!.Slots[slot].GroupShardingSlotNumber!.Value;

        var lane = new Envelope(new PostEntry(accountId, 0)).SlotForProcessing(lanes, rules);
        return (slot, lane);
    }

    private static IEnumerable<string> candidateAccountIds() =>
        Enumerable.Range(0, 2000).Select(i => $"account-{i:D4}");

    private string findAccountIdOwnedBy(IHost owner, Dictionary<int, IHost> owners)
    {
        var rules = _nodeA.Services.GetRequiredService<IWolverineRuntime>().Options.MessagePartitioning;

        foreach (var candidate in candidateAccountIds())
        {
            var slot = new Envelope(new PostEntry(candidate, 0)).SlotForSending(SlotCount, rules);
            if (owners.TryGetValue(slot, out var host) && ReferenceEquals(host, owner))
            {
                return candidate;
            }
        }

        throw new InvalidOperationException(
            $"No candidate account id hashed to a slot owned by the requested node. Ownership: {describeOwnership()}");
    }
}

public record NodeName(string Value);

/// <param name="NestedAccountId">
/// When set, the handler makes a nested awaited invocation for this account id from inside its own execution.
/// </param>
public record PostEntry(string AccountId, int Amount, string? NestedAccountId = null);

public record NestedEntry(string AccountId);

public record NestedEntryPosted(string AccountId, string HandledBy);

public record PostBadEntry(string AccountId);

public record EntryPosted(string AccountId, int Amount, string HandledBy);

public static class LedgerHandler
{
    private static int _inFlight;

    public static int MaxConcurrency;
    public static readonly ConcurrentBag<string> Handled = new();

    public static string? NestedOutcome;
    public static string? NestedMessage;
    public static TimeSpan NestedElapsed;

    public static void Reset()
    {
        _inFlight = 0;
        MaxConcurrency = 0;
        Handled.Clear();
        NestedOutcome = null;
        NestedMessage = null;
        NestedElapsed = TimeSpan.Zero;
    }

    public static async Task<EntryPosted> Handle(PostEntry command, NodeName node, IMessageContext bus)
    {
        var observed = Interlocked.Increment(ref _inFlight);
        trackConcurrency(observed);
        try
        {
            Handled.Add($"{node.Value}:{command.AccountId}");

            if (command.NestedAccountId is { } nested)
            {
                await invokeNestedAsync(nested, bus);
            }

            await Task.Delay(150);
            return new EntryPosted(command.AccountId, command.Amount, node.Value);
        }
        finally
        {
            Interlocked.Decrement(ref _inFlight);
        }
    }

    public static NestedEntryPosted Handle(NestedEntry command, NodeName node) =>
        new(command.AccountId, node.Value);

    private static async Task invokeNestedAsync(string nestedAccountId, IMessageContext bus)
    {
        var stopwatch = System.Diagnostics.Stopwatch.StartNew();
        try
        {
            await bus.InvokeAsync<NestedEntryPosted>(new NestedEntry(nestedAccountId),
                new DeliveryOptions { InvokeThroughRouting = true }, CancellationToken.None, 30.Seconds());
            NestedOutcome = "succeeded";
        }
        catch (Exception e)
        {
            NestedOutcome = e.GetType().Name;
            NestedMessage = e.Message;
        }
        finally
        {
            NestedElapsed = stopwatch.Elapsed;
        }
    }

    public static EntryPosted Handle(PostBadEntry command) =>
        throw new InvalidOperationException("this entry cannot be posted");

    private static void trackConcurrency(int observed)
    {
        int current;
        while ((current = Volatile.Read(ref MaxConcurrency)) < observed)
        {
            if (Interlocked.CompareExchange(ref MaxConcurrency, observed, current) == current) return;
        }
    }
}
