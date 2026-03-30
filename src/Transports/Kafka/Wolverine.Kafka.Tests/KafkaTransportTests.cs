using Confluent.Kafka.Admin;
using Shouldly;
using Wolverine.Configuration;
using Wolverine.Kafka.Internals;

namespace Wolverine.Kafka.Tests;

public class KafkaTopicGroupConfigurationTests
{
    private KafkaTopicGroup BuildGroup(params string[] topics)
    {
        var transport = new KafkaTransport();
        var group = new KafkaTopicGroup(transport, topics, EndpointRole.Application);
        return group;
    }

    [Fact]
    public void specification_uniform_sets_config_on_group()
    {
        var group = BuildGroup("topic-a", "topic-b");
        new KafkaTopicGroupListenerConfiguration(group)
            .Specification(spec => spec.NumPartitions = 12);

        var capturedSpec = new TopicSpecification { Name = "topic-a" };
        group.SpecificationConfig.ShouldNotBeNull();
        group.SpecificationConfig!("topic-a", capturedSpec);
        capturedSpec.NumPartitions.ShouldBe(12);
    }

    [Fact]
    public void specification_per_topic_receives_topic_name()
    {
        var group = BuildGroup("topic-a", "topic-b");
        new KafkaTopicGroupListenerConfiguration(group)
            .Specification((topicName, spec) => spec.NumPartitions = topicName == "topic-a" ? 6 : 24);

        group.SpecificationConfig.ShouldNotBeNull();

        var specA = new TopicSpecification { Name = "topic-a" };
        group.SpecificationConfig!("topic-a", specA);
        specA.NumPartitions.ShouldBe(6);

        var specB = new TopicSpecification { Name = "topic-b" };
        group.SpecificationConfig!("topic-b", specB);
        specB.NumPartitions.ShouldBe(24);
    }

    [Fact]
    public void topic_creation_sets_func_on_group()
    {
        var group = BuildGroup("topic-a", "topic-b");
        Func<Confluent.Kafka.IAdminClient, string, Task> func = (_, _) => Task.CompletedTask;

        new KafkaTopicGroupListenerConfiguration(group)
            .TopicCreation(func);

        group.CreateTopicFunc.ShouldNotBeNull();
        group.CreateTopicFunc.ShouldBeSameAs(func);
    }

    [Fact]
    public void specification_null_throws()
    {
        var group = BuildGroup("topic-a");
        Should.Throw<ArgumentNullException>(() =>
            new KafkaTopicGroupListenerConfiguration(group)
                .Specification((Action<TopicSpecification>)null!));
    }

    [Fact]
    public void specification_per_topic_null_throws()
    {
        var group = BuildGroup("topic-a");
        Should.Throw<ArgumentNullException>(() =>
            new KafkaTopicGroupListenerConfiguration(group)
                .Specification((Action<string, TopicSpecification>)null!));
    }

    [Fact]
    public void topic_creation_null_throws()
    {
        var group = BuildGroup("topic-a");
        Should.Throw<ArgumentNullException>(() =>
            new KafkaTopicGroupListenerConfiguration(group)
                .TopicCreation(null!));
    }
}

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

    
}