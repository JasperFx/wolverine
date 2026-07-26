using Shouldly;
using Wolverine.Configuration;
using Wolverine.Pubsub.Internal;
using Wolverine.Runtime.Partitioning;
using Xunit;

namespace Wolverine.Pubsub.Tests;

// GH-3632: PublishToShardedPubsubTopics / UseShardedPubsubTopics had no named-broker equivalent, because
// PartitionedMessageTopologyWithTopics always resolved the default/unnamed PubsubTransport.
public class ShardedTopicsNamedBrokerTests
{
    private static readonly BrokerName TheName = new("americas");

    [Fact]
    public void sharded_topology_without_a_broker_name_uses_the_default_transport()
    {
        var options = new WolverineOptions();
        options.UsePubsub("wolverine");

        var topology = new PartitionedMessageTopologyWithTopics(options, PartitionSlots.Five, "orders", 4);

        topology.Slots.Count.ShouldBe(4);
        topology.Slots.ShouldAllBe(x => x.Uri.Scheme == PubsubTransport.ProtocolName);
    }

    [Fact]
    public void sharded_topology_with_a_broker_name_targets_the_named_transport()
    {
        var options = new WolverineOptions();
        options.UsePubsub("wolverine");
        options.AddNamedPubsubBroker(TheName, "wolverine2");

        var topology = new PartitionedMessageTopologyWithTopics(options, PartitionSlots.Five, "orders", 4, TheName);

        topology.Slots.Count.ShouldBe(4);
        topology.Slots.ShouldAllBe(x => x.Uri.Scheme == TheName.Name);
        topology.Slots.Select(x => x.Uri).ShouldBe(new[]
        {
            new Uri("americas://wolverine2/orders1"),
            new Uri("americas://wolverine2/orders2"),
            new Uri("americas://wolverine2/orders3"),
            new Uri("americas://wolverine2/orders4")
        });
    }

    [Fact]
    public void publish_to_sharded_pubsub_topics_on_named_broker_targets_the_named_transport()
    {
        var options = new WolverineOptions();
        options.UsePubsub("wolverine");
        options.AddNamedPubsubBroker(TheName, "wolverine2");

        options.MessagePartitioning.ByMessage<OrderPlaced>(x => x.OrderId.ToString());
        options.MessagePartitioning.PublishToShardedPubsubTopicsOnNamedBroker(TheName, "orders", 4,
            topology => topology.Message<OrderPlaced>());

        var named = options.Transports.OfType<PubsubTransport>().Single(x => x.Protocol == TheName.Name);
        named.Topics.Select(x => x.EndpointName).OrderBy(x => x)
            .ShouldBe(["orders1", "orders2", "orders3", "orders4"]);
    }

    [Fact]
    public void use_sharded_pubsub_topics_on_named_broker_targets_the_named_transport()
    {
        var options = new WolverineOptions();
        options.UsePubsub("wolverine");
        options.AddNamedPubsubBroker(TheName, "wolverine2");

        options.MessagePartitioning.ByMessage<OrderPlaced>(x => x.OrderId.ToString());
        options.MessagePartitioning.GlobalPartitioned(topology =>
        {
            topology.UseShardedPubsubTopicsOnNamedBroker(TheName, "orders", 4);
            topology.MessagesImplementing<OrderPlaced>();
        });

        var named = options.Transports.OfType<PubsubTransport>().Single(x => x.Protocol == TheName.Name);
        named.Topics.Select(x => x.EndpointName).OrderBy(x => x)
            .ShouldBe(["orders1", "orders2", "orders3", "orders4"]);
    }
}

public record OrderPlaced(Guid OrderId);
