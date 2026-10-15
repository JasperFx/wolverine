using JasperFx.Events;
using JasperFx.Events.Descriptors;
using JasperFx.Events.Projections;
using NSubstitute;

namespace CoreTests.Runtime.Agents;

internal static class FakeEventStoreUsage
{
    /// <summary>
    /// Stub a substitute <see cref="IEventStore" /> as a real store answers from one usage descriptor: the full
    /// <see cref="IEventStore.TryCreateUsage" />, and the two cheap reads EventStoreAgents makes on the heartbeat
    /// (GH-5002) -- the database half, and the async shard names. NSubstitute intercepts default interface members
    /// too, so without this they would answer null and an empty list rather than falling back to the usage.
    /// </summary>
    public static IEventStore StubUsage(this IEventStore store, EventStoreUsage? usage)
    {
        store.TryCreateUsage(Arg.Any<CancellationToken>()).Returns(Task.FromResult(usage));
        store.TryDescribeDatabasesAsync(Arg.Any<CancellationToken>()).Returns(Task.FromResult(usage?.Database));
        store.RegisteredShardNames().Returns(usage == null
            ? []
            : usage.Subscriptions
                .Where(x => x.Lifecycle == ProjectionLifecycle.Async)
                .SelectMany(x => x.ShardNames)
                .ToArray());

        return store;
    }
}
