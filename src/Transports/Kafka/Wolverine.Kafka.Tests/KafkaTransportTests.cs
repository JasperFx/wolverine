using Shouldly;
using Wolverine.Configuration;
using Wolverine.Kafka.Internals;

namespace Wolverine.Kafka.Tests;

public class KafkaTransportTests
{
    [Theory]
    [InlineData("kafka://topic/one", "one")]
    [InlineData("kafka://topic/one.two", "one.two")]
    [InlineData("kafka://topic/one.two/", "one.two")]
    [InlineData("kafka://topic/one.two.three", "one.two.three")]
    public void get_topic_name_from_uri(string uriString, string expected)
    {
        KafkaTopic.TopicNameForUri(new Uri(uriString))
            .ShouldBe(expected);
    }

    [Fact]
    public void build_uri_for_endpoint()
    {
        var transport = new KafkaTransport();
        new KafkaTopic(transport, "one.two", EndpointRole.Application)
            .Uri.ShouldBe(new Uri("Kafka://topic/one.two"));
    }

    [Fact]
    public void endpoint_name_is_topic_name()
    {
        var transport = new KafkaTransport();
        new KafkaTopic(transport, "one.two", EndpointRole.Application)
            .EndpointName.ShouldBe("one.two");
    }

    [Fact]
    public void produce_and_consume_by_default()
    {
        new KafkaTransport().Usage.ShouldBe(KafkaUsage.ProduceAndConsume);
    }

    [Fact]
    public void enable_at_least_once_delivery_sets_property_on_topic()
    {
        var transport = new KafkaTransport();
        var topic = new KafkaTopic(transport, "test-topic", EndpointRole.Application);

        topic.EnableAtLeastOnceDelivery.ShouldBeFalse();

        var config = new KafkaListenerConfiguration(topic);
        config.EnableAtLeastOnceDelivery();

        // Apply the delayed configuration
        ((IDelayedEndpointConfiguration)config).Apply();

        topic.EnableAtLeastOnceDelivery.ShouldBeTrue();
    }
}