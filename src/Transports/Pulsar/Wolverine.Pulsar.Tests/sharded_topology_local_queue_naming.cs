using Shouldly;
using Wolverine.Configuration;
using Wolverine.Runtime.Partitioning;
using Xunit;

namespace Wolverine.Pulsar.Tests;

// GH-3467. Pulsar topics are addressed by their full path, but that whole string was being used
// verbatim as the companion local queue base name -- producing local queues named
// "global-persistent://public/default/orders1".
public class sharded_topology_local_queue_naming
{
    [Theory]
    [InlineData("persistent://public/default/orders", "orders")]
    [InlineData("non-persistent://tenant1/ns1/orders", "orders")]
    [InlineData("orders", "orders")]
    public void local_queue_base_name_is_the_topic_short_name(string topicPath, string expected)
    {
        PulsarTransportExtensions.LocalQueueBaseNameFor(topicPath).ShouldBe(expected);
    }

    [Fact]
    public void companion_local_queues_are_named_off_the_short_topic_name()
    {
        var options = new WolverineOptions();
        options.UsePulsar();

        options.MessagePartitioning.ByMessage<ShardedOrderPlaced>(x => x.OrderId.ToString());
        options.MessagePartitioning.GlobalPartitioned(topology =>
        {
            topology.UseShardedPulsarTopics("persistent://public/default/orders", 4);
            topology.Message<ShardedOrderPlaced>();
        });

        options.MessagePartitioning.GlobalPartitionedTopologies.Single().LocalTopology!.Slots.Select(x => x.Uri)
            .ShouldBe([
                new Uri("local://global-orders1/"),
                new Uri("local://global-orders2/"),
                new Uri("local://global-orders3/"),
                new Uri("local://global-orders4/")
            ]);
    }
}

public record ShardedOrderPlaced(Guid OrderId);
