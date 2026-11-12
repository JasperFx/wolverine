using Dekaf;
using Dekaf.Consumer;

namespace Wolverine.Kafka.Internals;

/// <summary>
/// Forwards a consumer's rebalance callbacks to its listener's offset committer. The relay is registered when the
/// consumer is built, before the listener and its committer exist.
/// </summary>
internal sealed class KafkaRebalanceRelay : IRebalanceListener
{
    public KafkaOffsetCommitter? Committer { get; set; }

    public ValueTask OnPartitionsAssignedAsync(IEnumerable<TopicPartition> partitions,
        CancellationToken cancellationToken)
    {
        // The committer counts a partition as owned again once one of its records is delivered
        return ValueTask.CompletedTask;
    }

    public ValueTask OnPartitionsRevokedAsync(IEnumerable<TopicPartition> partitions,
        CancellationToken cancellationToken)
    {
        var committer = Committer;
        return committer == null ? ValueTask.CompletedTask : committer.PartitionsRevokedAsync(partitions);
    }

    public ValueTask OnPartitionsLostAsync(IEnumerable<TopicPartition> partitions, CancellationToken cancellationToken)
    {
        Committer?.PartitionsLost(partitions);
        return ValueTask.CompletedTask;
    }
}
