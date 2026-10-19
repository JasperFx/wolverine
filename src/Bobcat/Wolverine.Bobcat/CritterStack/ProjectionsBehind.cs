namespace Bobcat.CritterStack;

/// <summary>
/// bobcat#686. What <see cref="EventStores.DescribeProjectionsBehindAsync" /> could find out: the shards that had not
/// caught up, as <c>'shard' at N of M</c> (M is the highest event sequence number, not an event count), and the
/// exception that stopped the store saying more, or null when it said everything it could.
/// </summary>
public sealed record ProjectionsBehind(IReadOnlyList<string> Shards, Exception? Failure);
