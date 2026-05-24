using Microsoft.Extensions.Logging.Abstractions;
using Shouldly;
using Wolverine.AmazonSqs.Internal;
using Wolverine.ComplianceTests;
using Wolverine.Configuration;

namespace Wolverine.AmazonSqs.Tests.Internal;

// GH-2886: standard (non-FIFO) SQS queues can opt into AWS "fair queues" by mapping
// Envelope.GroupId onto the SQS MessageGroupId. The mapping flows through the envelope
// mapper (ISqsEnvelopeMapper.DetermineGroupId) and is gated per-endpoint by
// EnableFairQueueMessageGroups(). FIFO queues keep their existing group + deduplication
// behavior unconditionally; standard queues never set a deduplication id.
public class sqs_fair_queue_message_groups
{
    private static AmazonSqsQueue QueueFor(string name, bool enableFairQueues)
    {
        var queue = new AmazonSqsQueue(name, new AmazonSqsTransport())
        {
            Mapper = new DefaultSqsEnvelopeMapper(),
            EnableFairQueueMessageGroups = enableFairQueues
        };

        return queue;
    }

    private static SendMessageBatchRequestEntryView FirstEntry(AmazonSqsQueue queue, Envelope envelope)
    {
        var batch = new OutgoingSqsBatch(queue, NullLogger.Instance, [envelope]);
        var entry = batch.Request.Entries.ShouldHaveSingleItem();
        return new SendMessageBatchRequestEntryView(entry.MessageGroupId, entry.MessageDeduplicationId);
    }

    private record SendMessageBatchRequestEntryView(string? MessageGroupId, string? MessageDeduplicationId);

    [Fact]
    public void default_mapper_maps_group_id_from_envelope()
    {
        var envelope = ObjectMother.Envelope();
        envelope.GroupId = "tenant-1";

        new DefaultSqsEnvelopeMapper().DetermineGroupId(envelope).ShouldBe("tenant-1");
    }

    [Fact]
    public void standard_queue_without_opt_in_does_not_set_message_group_id()
    {
        var envelope = ObjectMother.Envelope();
        envelope.GroupId = "tenant-1";

        FirstEntry(QueueFor("standard", enableFairQueues: false), envelope)
            .MessageGroupId.ShouldBeNull();
    }

    [Fact]
    public void standard_queue_with_opt_in_sets_message_group_id()
    {
        var envelope = ObjectMother.Envelope();
        envelope.GroupId = "tenant-1";

        FirstEntry(QueueFor("standard", enableFairQueues: true), envelope)
            .MessageGroupId.ShouldBe("tenant-1");
    }

    [Fact]
    public void standard_queue_with_opt_in_but_no_group_id_leaves_it_unset()
    {
        var envelope = ObjectMother.Envelope();
        envelope.GroupId = null;

        FirstEntry(QueueFor("standard", enableFairQueues: true), envelope)
            .MessageGroupId.ShouldBeNull();
    }

    [Fact]
    public void standard_queue_never_sets_deduplication_id()
    {
        // Deduplication is a FIFO-only concept. A standard fair queue must not receive one
        // even when the envelope happens to carry a deduplication id.
        var envelope = ObjectMother.Envelope();
        envelope.GroupId = "tenant-1";
        envelope.DeduplicationId = "dedup-1";

        FirstEntry(QueueFor("standard", enableFairQueues: true), envelope)
            .MessageDeduplicationId.ShouldBeNull();
    }

    [Fact]
    public void fifo_queue_sets_group_and_deduplication_regardless_of_opt_in()
    {
        // FIFO behavior is unchanged: MessageGroupId + MessageDeduplicationId are always mapped,
        // independent of the EnableFairQueueMessageGroups() flag.
        var envelope = ObjectMother.Envelope();
        envelope.GroupId = "tenant-1";
        envelope.DeduplicationId = "dedup-1";

        var view = FirstEntry(QueueFor("orders.fifo", enableFairQueues: false), envelope);

        view.MessageGroupId.ShouldBe("tenant-1");
        view.MessageDeduplicationId.ShouldBe("dedup-1");
    }

    [Fact]
    public void enable_fair_queue_message_groups_configuration_sets_flag()
    {
        var queue = new AmazonSqsQueue("standard", new AmazonSqsTransport());
        var configuration = new AmazonSqsSubscriberConfiguration(queue);

        configuration.EnableFairQueueMessageGroups();
        ((IDelayedEndpointConfiguration)configuration).Apply();

        queue.EnableFairQueueMessageGroups.ShouldBeTrue();
    }
}
