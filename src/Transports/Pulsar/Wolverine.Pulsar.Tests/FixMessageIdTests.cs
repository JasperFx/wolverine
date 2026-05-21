using DotPulsar;
using Shouldly;
using Xunit;

namespace Wolverine.Pulsar.Tests;

public class FixMessageIdTests
{
    private const string TopicUri = "persistent://public/default/events";

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
}
