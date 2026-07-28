using System.Collections.Concurrent;
using Shouldly;
using Wolverine.Tracking;

namespace Wolverine.ComplianceTests.Partitioning;

/// <summary>
/// A minimal, transport-agnostic scenario for the
/// <see href="https://wolverinefx.net/guide/messaging/partitioning.html#global-partitioning">global partitioning</see>
/// topology: publish a burst of grouped messages, then assert that every slot was used and that no
/// group id straddled two slots.
///
/// Transport suites supply only the `UseSharded*()` call -- everything else (message types, handler,
/// grouping rule, publishing burst, assertions) lives here so a new transport costs one small test
/// class. See GH-3467.
/// </summary>
public static class ShardedProcessing
{
    /// <summary>
    /// Set up the partitioning rules for <see cref="IShardedLetter" /> and register the handler.
    /// Call this, then add your transport's `UseSharded*()` inside the <paramref name="configureTopology" />.
    /// </summary>
    public static void UseShardedLetters(this WolverineOptions opts,
        Action<Runtime.Partitioning.GlobalPartitionedMessageTopology> configureTopology)
    {
        ShardedLetterHandler.Received.Clear();

        opts.Discovery.DisableConventionalDiscovery().IncludeType(typeof(ShardedLetterHandler));

        opts.MessagePartitioning.ByMessage<IShardedLetter>(x => x.Id.ToString());

        opts.MessagePartitioning.GlobalPartitioned(topology =>
        {
            configureTopology(topology);
            topology.MessagesImplementing<IShardedLetter>();
        });
    }

    /// <summary>
    /// Publish 25 group ids concurrently, four messages each. Pass this straight to
    /// <c>ExecuteAndWaitAsync()</c>.
    /// </summary>
    public static async Task PumpOutLetters(IMessageContext bus)
    {
        var tasks = new Task[5];
        for (var i = 0; i < tasks.Length; i++)
        {
            tasks[i] = Task.Run(async () =>
            {
                for (var j = 0; j < 5; j++)
                {
                    var id = Guid.NewGuid();

                    await bus.PublishAsync(new ShardedLetterA(id));
                    await bus.PublishAsync(new ShardedLetterB(id));
                    await bus.PublishAsync(new ShardedLetterC(id));
                    await bus.PublishAsync(new ShardedLetterD(id));
                }
            });
        }

        await Task.WhenAll(tasks);
    }

    /// <summary>
    /// Assert that every companion local queue was executed against. In single node mode global
    /// partitioning shortcuts straight to the companion local queues, since this node owns every
    /// exclusive listener -- so this is the assertion that the slot fan-out really happened.
    /// </summary>
    public static void AssertEveryShardWasUsed(ITrackedSession tracked, string baseName, int numberOfSlots)
    {
        var destinations = tracked.Executed.Envelopes().Select(x => x.Destination).ToArray();

        var missing = Enumerable.Range(1, numberOfSlots)
            .Select(i => new Uri($"local://global-{baseName}{i}/"))
            .Where(uri => !destinations.Contains(uri))
            .ToArray();

        missing.ShouldBeEmpty(
            $"Expected every companion local queue to be used. Saw: {string.Join(", ", destinations.Distinct())}");
    }

    /// <summary>
    /// The actual promise of global partitioning: all messages carrying one group id are processed
    /// by one slot, so they can never run concurrently with each other.
    /// </summary>
    public static void AssertGroupsNeverStraddleSlots()
    {
        ShardedLetterHandler.Received.Count.ShouldBeGreaterThan(0);

        foreach (var group in ShardedLetterHandler.Received.GroupBy(x => x.Id))
        {
            group.Select(x => x.Destination).Distinct().Count()
                .ShouldBe(1, $"Group id {group.Key} was processed by more than one slot");
        }
    }
}

public interface IShardedLetter
{
    Guid Id { get; }
}

public record ShardedLetterA(Guid Id) : IShardedLetter;

public record ShardedLetterB(Guid Id) : IShardedLetter;

public record ShardedLetterC(Guid Id) : IShardedLetter;

public record ShardedLetterD(Guid Id) : IShardedLetter;

public static class ShardedLetterHandler
{
    public static readonly ConcurrentBag<(Guid Id, Uri? Destination)> Received = new();

    public static void Handle(ShardedLetterA message, Envelope envelope) =>
        Received.Add((message.Id, envelope.Destination));

    public static void Handle(ShardedLetterB message, Envelope envelope) =>
        Received.Add((message.Id, envelope.Destination));

    public static void Handle(ShardedLetterC message, Envelope envelope) =>
        Received.Add((message.Id, envelope.Destination));

    public static void Handle(ShardedLetterD message, Envelope envelope) =>
        Received.Add((message.Id, envelope.Destination));
}
