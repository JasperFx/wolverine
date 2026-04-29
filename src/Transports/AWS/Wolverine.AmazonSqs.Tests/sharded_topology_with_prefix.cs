using Shouldly;
using Wolverine.AmazonSqs.Internal;

namespace Wolverine.AmazonSqs.Tests;

public interface IShardedPrefixTestMessage
{
    string GroupId { get; }
}

public record ShardedPrefixTestPayload(string GroupId) : IShardedPrefixTestMessage;

public class sharded_topology_with_prefix
{
    // Regression: GlobalPartitioned with UseShardedAmazonSqsQueues used to register
    // slot endpoints with the un-prefixed base name, bypassing PrefixIdentifiers.
    // At broker startup the slot endpoint's GetQueueUrl call would then look up
    // "<baseName><N>" in AWS instead of "<prefix>-<baseName><N>" and crash with
    // QueueDoesNotExistException, taking the whole transport down.
    [Fact]
    public void prefix_is_applied_to_sharded_slot_endpoints()
    {
        var options = new WolverineOptions();
        options.UseAmazonSqsTransport().PrefixIdentifiers("foo");

        options.MessagePartitioning.ByMessage<IShardedPrefixTestMessage>(x => x.GroupId);

        options.MessagePartitioning.GlobalPartitioned(topology =>
        {
            topology.UseShardedAmazonSqsQueues("orders", 4);
            topology.MessagesImplementing<IShardedPrefixTestMessage>();
        });

        var transport = options.AmazonSqsTransport();
        var queueNames = transport.Queues.Select(q => q.QueueName).ToList();

        queueNames.ShouldContain("foo-orders1");
        queueNames.ShouldContain("foo-orders2");
        queueNames.ShouldContain("foo-orders3");
        queueNames.ShouldContain("foo-orders4");

        queueNames.ShouldNotContain("orders1");
        queueNames.ShouldNotContain("orders2");
        queueNames.ShouldNotContain("orders3");
        queueNames.ShouldNotContain("orders4");
    }
}
