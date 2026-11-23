using Wolverine.Runtime.Partitioning;

namespace Wolverine.ComplianceTests.Partitioning;

/// <summary>
/// GH-5736. The sharded processing tests assert that every shard executed something, which only holds if the group
/// ids they publish actually land on every shard. Twenty-five random Guids miss one of four shards about 0.3% of the
/// time -- (3/4)^25 for each shard -- so the assertion failed now and then while every message that was sent had
/// executed. Choosing the ids with the same slot calculation the sharded route uses keeps the assertion and removes
/// the dice roll.
/// </summary>
public static class ShardCoveringGroupIds
{
    /// <summary>
    /// <paramref name="count"/> group ids, spread round-robin over every one of <paramref name="numberOfShards"/>
    /// shards as <see cref="PartitionedMessagingExtensions.SlotForSending"/> assigns them
    /// </summary>
    /// <param name="rules">The application's own partitioning rules, which decide the group id of a message</param>
    /// <param name="numberOfShards">How many shards the topology has</param>
    /// <param name="count">How many ids to return</param>
    /// <param name="messageFor">Builds a message carrying the id, so the rules can derive its group id</param>
    public static Guid[] CoveringEveryShard(MessagePartitioningRules rules, int numberOfShards, int count,
        Func<Guid, object> messageFor)
    {
        var ids = new Guid[count];
        for (var i = 0; i < count; i++)
        {
            var shard = i % numberOfShards;
            Guid id;
            do
            {
                id = Guid.NewGuid();
            } while (new Envelope(messageFor(id)).SlotForSending(numberOfShards, rules) != shard);

            ids[i] = id;
        }

        return ids;
    }
}
