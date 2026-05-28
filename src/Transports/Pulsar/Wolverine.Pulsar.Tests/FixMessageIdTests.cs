using DotPulsar;
using Shouldly;
using Xunit;

namespace Wolverine.Pulsar.Tests;

// Regression for the DotPulsar batch-handler partitioned-topic bug
// (https://github.com/apache/pulsar-dotpulsar/issues/287). Builds on @patrick-cloke-simplisafe's
// original repro in PR #2883; the only structural difference here is that PulsarListener now
// reconstructs the partition sub-topic URI from the source consumer's own Topic
// (IConsumer.Topic) rather than from _endpoint.PulsarTopic(). That additionally covers the
// retry-consumer ack path - the retry consumer is subscribed to {baseTopic}-RETRY (or a
// user-configured retry topic), so reusing the listener's base endpoint URI would have
// reconstructed wrong sub-topic names and produced the same KeyNotFoundException on partitioned
// retry topics.
public class FixMessageIdTests
{
    private const string TopicUri = "persistent://public/default/events";
    private const string RetryTopicUri = "persistent://public/default/events-RETRY";

    [Fact]
    public void FixMessageId_BatchMessageMissingTopic_ReconstructsPartitionTopic()
    {
        var messageId = new MessageId(1UL, 2UL, partition: 3, batchIndex: 0);

        var result = PulsarListener.FixMessageId(messageId, TopicUri);

        result.Topic.ShouldBe($"{TopicUri}-partition-3");
        result.LedgerId.ShouldBe(1UL);
        result.EntryId.ShouldBe(2UL);
        result.Partition.ShouldBe(3);
        result.BatchIndex.ShouldBe(0);
    }

    [Fact]
    public void FixMessageId_TopicAlreadySet_ReturnsOriginal()
    {
        var messageId = new MessageId(1UL, 2UL, partition: 3, batchIndex: 0, topic: $"{TopicUri}-partition-3");

        var result = PulsarListener.FixMessageId(messageId, TopicUri);

        result.ShouldBeSameAs(messageId);
    }

    [Fact]
    public void FixMessageId_NonPartitionedMessage_ReturnsOriginal()
    {
        var messageId = new MessageId(1UL, 2UL, partition: -1, batchIndex: -1);

        var result = PulsarListener.FixMessageId(messageId, TopicUri);

        result.ShouldBeSameAs(messageId);
    }

    [Fact]
    public void FixMessageId_BatchMessageOnPartitionedRetryTopic_UsesRetryTopicForReconstruction()
    {
        // When the source consumer is the retry consumer, the listener passes that consumer's
        // Topic - which is the retry topic URI, not the base topic URI. The reconstruction must
        // therefore key the partition sub-topic off the retry topic so the ack lands on the
        // correct sub-consumer inside DotPulsar's IConsumer._subConsumers map. Passing the base
        // topic URI here (as the prior reconstruction did) would produce
        // "events-partition-3" — a key the retry consumer's sub-consumers map does not contain —
        // and re-trigger the same KeyNotFoundException the original bug reported on the main
        // ack path.
        var messageId = new MessageId(1UL, 2UL, partition: 3, batchIndex: 0);

        var result = PulsarListener.FixMessageId(messageId, RetryTopicUri);

        result.Topic.ShouldBe($"{RetryTopicUri}-partition-3");
        result.Partition.ShouldBe(3);
    }
}
