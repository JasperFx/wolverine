using System.Collections.Concurrent;
using JasperFx.Core;
using Microsoft.Extensions.DependencyInjection;
using Shouldly;

namespace Wolverine.ComplianceTests.Partitioning;

/// <summary>
/// GH-3709. The transport-agnostic harness for <c>ProcessInParallelWithNativeAcks()</c> on a global
/// partitioned topology, in the same spirit as <see cref="ShardedProcessing" /> (GH-3467): a transport
/// suite supplies only its own <c>UseSharded*()</c> call and the cluster shape, and everything else --
/// the message type, the handler, the group ledger, the publishing burst, the assertions -- lives here.
/// </summary>
/// <remarks>
/// <para><b>The guarantee under test, stated exactly:</b> no two messages sharing a group id execute
/// concurrently. Within a node the sequential lane inside the slot's own receiver enforces it; across the
/// cluster the exclusive slot listener enforces it, because exactly one node consumes a given slot.</para>
///
/// <para>Ordering is per-slot best effort, <b>not</b> per-group guaranteed: redelivery or requeue may
/// reorder, and the ordering unit is the <b>slot</b>, not the group -- two groups hashing to the same slot
/// serialize against each other. Nothing here asserts ordering.</para>
///
/// <para>Delivery is at-least-once and owned by the broker rather than by the inbox, so the completeness
/// assertion is <see cref="AssertEveryLetterWasHandled" /> -- every published letter seen <i>at least</i>
/// once. Duplicates are legal.</para>
/// </remarks>
public static class NativeAckPartitionedProcessing
{
    /// <summary>
    /// The shared, cluster-wide ledger. Every host in a multi-node test runs in this same process, which is
    /// exactly what makes a genuinely cluster-wide concurrency assertion possible.
    /// </summary>
    public static GroupConcurrencyLedger Ledger { get; } = new();

    /// <summary>
    /// How long each handler holds its group. This is the width of the window a concurrency violation has to
    /// land in, so a test that wants to *catch* overlap needs it comfortably longer than the broker round trip.
    /// </summary>
    public static TimeSpan Dwell { get; set; } = 50.Milliseconds();

    /// <summary>
    /// Set up partitioning rules and handler discovery for <see cref="NativeAckLetter" />, then call the
    /// transport's own <c>UseSharded*()</c> plus <c>ProcessInParallelWithNativeAcks()</c> inside
    /// <paramref name="configureTopology" />.
    /// </summary>
    public static void UseNativeAckLetters(this WolverineOptions opts, string nodeName,
        Action<Runtime.Partitioning.GlobalPartitionedMessageTopology> configureTopology)
    {
        opts.ServiceName = nodeName;

        opts.Discovery.DisableConventionalDiscovery().IncludeType(typeof(NativeAckLetterHandler));
        opts.Services.AddSingleton(new NativeAckNodeMarker(nodeName));

        opts.MessagePartitioning.ByMessage<NativeAckLetter>(x => x.GroupId);

        opts.MessagePartitioning.GlobalPartitioned(topology =>
        {
            configureTopology(topology);
            topology.Message<NativeAckLetter>();
        });
    }

    /// <summary>
    /// Publish <paramref name="messagesPerGroup" /> letters for each of <paramref name="groupCount" /> group
    /// ids, spreading the publishing across every host so no single node is the sole producer.
    /// </summary>
    /// <param name="busSources">
    /// One factory per host, not one bus per host: an <see cref="IMessageBus" /> is scoped and is not built to
    /// be shared across concurrent invocations, so each parallel publisher below resolves its own.
    /// </param>
    public static async Task<IReadOnlyList<(string GroupId, int Sequence)>> PumpOutLettersAsync(
        IReadOnlyList<Func<IMessageBus>> busSources, int groupCount, int messagesPerGroup)
    {
        var published = new ConcurrentQueue<(string, int)>();

        var groups = Enumerable.Range(0, groupCount).Select(_ => Guid.NewGuid().ToString()).ToArray();

        await Parallel.ForEachAsync(groups, async (groupId, _) =>
        {
            var buses = busSources.Select(x => x()).ToArray();

            for (var i = 0; i < messagesPerGroup; i++)
            {
                // Round robin the producer so the send path -- not just the receive path -- is exercised
                // from more than one node.
                var bus = buses[Math.Abs(groupId.GetHashCode() + i) % buses.Length];
                await bus.PublishAsync(new NativeAckLetter(groupId, i));
                published.Enqueue((groupId, i));
            }
        });

        return published.ToArray();
    }

    /// <summary>
    /// Poll until every published letter has been handled at least once, or the timeout expires. Returns
    /// true if everything landed. Polling rather than a tracked session because the multi-node scenarios
    /// deliberately stop a host mid-stream.
    /// </summary>
    public static async Task<bool> WaitForCompletionAsync(
        IReadOnlyCollection<(string GroupId, int Sequence)> published, TimeSpan timeout)
    {
        var deadline = DateTimeOffset.UtcNow.Add(timeout);
        while (DateTimeOffset.UtcNow < deadline)
        {
            if (Ledger.OutstandingFrom(published).Count == 0)
            {
                return true;
            }

            await Task.Delay(100.Milliseconds());
        }

        return Ledger.OutstandingFrom(published).Count == 0;
    }

    /// <summary>
    /// The heart of it: assert that no group id was ever executing in two places at once, anywhere in the
    /// cluster. See <see cref="GroupConcurrencyLedger" /> for how overlap is detected.
    /// </summary>
    public static void AssertNoIntraGroupConcurrency()
    {
        Ledger.Handled.Count.ShouldBeGreaterThan(0, "Nothing was handled at all, so the invariant is untested");

        Ledger.Violations.ShouldBeEmpty(
            "Two messages sharing a group id executed concurrently: " + Ledger.Violations.Join(" | "));
    }

    /// <summary>
    /// At-least-once completeness. Duplicates are expected and legal in this mode.
    /// </summary>
    public static void AssertEveryLetterWasHandled(IReadOnlyCollection<(string GroupId, int Sequence)> published)
    {
        var outstanding = Ledger.OutstandingFrom(published);

        outstanding.ShouldBeEmpty(
            $"{outstanding.Count} of {published.Count} published letters were never handled: "
            + outstanding.Take(10).Select(x => $"{x.GroupId}#{x.Sequence}").Join(", "));
    }

    /// <summary>
    /// Every slot in the topology must have actually executed something, otherwise a "no concurrency"
    /// result would be trivially satisfied by everything landing on one slot.
    /// </summary>
    public static void AssertEverySlotWasUsed(int numberOfSlots)
    {
        var destinations = Ledger.Handled.Select(x => x.Destination).Distinct().ToArray();

        destinations.Length.ShouldBe(numberOfSlots,
            $"Expected all {numberOfSlots} slots to be used. Saw: {destinations.Select(x => x?.ToString() ?? "null").Join(", ")}");
    }

    /// <summary>
    /// A group must never straddle two slots -- that is the routing half of the guarantee, and it is what
    /// makes the single exclusive consumer per slot sufficient for the cluster-wide half.
    /// </summary>
    public static void AssertGroupsNeverStraddleSlots()
    {
        foreach (var group in Ledger.Handled.GroupBy(x => x.GroupId))
        {
            group.Select(x => x.Destination).Distinct().Count()
                .ShouldBe(1, $"Group id {group.Key} was handled on more than one slot");
        }
    }
}

/// <summary>
/// <paramref name="Payload" /> is optional and carries nothing the harness reads. It exists so GH-3713's
/// webhook flood can send bodies in the size range a real webhook delivery has, rather than measuring broker
/// behaviour against an empty record.
/// </summary>
public record NativeAckLetter(string GroupId, int Sequence, string? Payload = null);

/// <summary>
/// Injected per host so the ledger can name which node executed a message. All hosts in a multi-node test
/// share one process, so the node name has to come from configuration rather than from the environment.
/// </summary>
public class NativeAckNodeMarker(string nodeName)
{
    public string NodeName { get; } = nodeName;
}

public static class NativeAckLetterHandler
{
    public static Task Handle(NativeAckLetter letter, Envelope envelope, NativeAckNodeMarker node)
    {
        return NativeAckPartitionedProcessing.Ledger.ExecuteAsync(letter, envelope.Destination, node.NodeName,
            NativeAckPartitionedProcessing.Dwell);
    }
}

/// <summary>
/// Detects overlapping execution of one group id across the whole cluster. A handler claims its group id on
/// entry and releases it on exit; a claim that finds the group already held is recorded as a violation.
/// </summary>
/// <remarks>
/// The release is a compare-and-remove on the claim token, not a blind remove, so a losing claimant can
/// never evict the rightful holder's entry and cascade one real violation into a string of phantom ones.
/// </remarks>
public sealed class GroupConcurrencyLedger
{
    private readonly ConcurrentDictionary<string, string> _inFlight = new();
    private readonly ConcurrentQueue<string> _violations = new();
    private readonly ConcurrentQueue<HandledLetter> _handled = new();

    public IReadOnlyList<string> Violations => _violations.ToArray();
    public IReadOnlyList<HandledLetter> Handled => _handled.ToArray();

    public void Clear()
    {
        _inFlight.Clear();
        _violations.Clear();
        _handled.Clear();
    }

    public async Task ExecuteAsync(NativeAckLetter letter, Uri? destination, string nodeName, TimeSpan dwell)
    {
        var claim = $"{nodeName}/{letter.Sequence}/{Guid.NewGuid():N}";

        if (!_inFlight.TryAdd(letter.GroupId, claim))
        {
            _inFlight.TryGetValue(letter.GroupId, out var holder);
            _violations.Enqueue(
                $"group {letter.GroupId} was held by {holder ?? "(released)"} when {claim} began executing on {destination}");
        }

        try
        {
            if (dwell > TimeSpan.Zero)
            {
                await Task.Delay(dwell);
            }

            _handled.Enqueue(new HandledLetter(letter.GroupId, letter.Sequence, nodeName, destination));
        }
        finally
        {
            _inFlight.TryRemove(new KeyValuePair<string, string>(letter.GroupId, claim));
        }
    }

    /// <summary>
    /// The published letters that have not been handled even once yet.
    /// </summary>
    public IReadOnlyList<(string GroupId, int Sequence)> OutstandingFrom(
        IReadOnlyCollection<(string GroupId, int Sequence)> published)
    {
        var seen = _handled.Select(x => (x.GroupId, x.Sequence)).ToHashSet();
        return published.Where(x => !seen.Contains(x)).ToArray();
    }

    public record HandledLetter(string GroupId, int Sequence, string NodeName, Uri? Destination);
}
