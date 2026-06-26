using Confluent.Kafka;
using Confluent.Kafka.Admin;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
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
        var config = new KafkaTopicGroupListenerConfiguration(group)
            .Specification(spec => spec.NumPartitions = 12);
        ((IDelayedEndpointConfiguration)config).Apply();

        var capturedSpec = new TopicSpecification { Name = "topic-a" };
        group.SpecificationConfig.ShouldNotBeNull();
        group.SpecificationConfig!("topic-a", capturedSpec);
        capturedSpec.NumPartitions.ShouldBe(12);
    }

    [Fact]
    public void specification_per_topic_receives_topic_name()
    {
        var group = BuildGroup("topic-a", "topic-b");
        var config = new KafkaTopicGroupListenerConfiguration(group)
            .Specification((topicName, spec) => spec.NumPartitions = topicName == "topic-a" ? 6 : 24);
        ((IDelayedEndpointConfiguration)config).Apply();

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

        var config = new KafkaTopicGroupListenerConfiguration(group)
            .TopicCreation(func);
        ((IDelayedEndpointConfiguration)config).Apply();

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

public class KafkaListenerConfigurationTests
{
    private static KafkaTopic BuildTopic()
    {
        var transport = new KafkaTransport();
        return new KafkaTopic(transport, "topic-a", EndpointRole.Application);
    }

    [Fact]
    public void extend_consumer_configuration_preserves_parent_consumer_configuration()
    {
        var topic = BuildTopic();
        topic.Parent.ConsumerConfig.BootstrapServers = "localhost:9092";
        topic.Parent.ConsumerConfig.ClientId = "global-client";

        var config = new KafkaListenerConfiguration(topic)
            .ExtendConsumerConfiguration(consumer => consumer.GroupId = "topic-group");

        ((IDelayedEndpointConfiguration)config).Apply();

        topic.ConsumerConfig.ShouldNotBeNull();
        topic.ConsumerConfig.BootstrapServers.ShouldBe("localhost:9092");
        topic.ConsumerConfig.ClientId.ShouldBe("global-client");
        topic.ConsumerConfig.GroupId.ShouldBe("topic-group");
    }

    [Fact]
    public void extend_consumer_configuration_preserves_existing_topic_consumer_configuration()
    {
        var topic = BuildTopic();
        topic.Parent.ConsumerConfig.BootstrapServers = "localhost:9092";
        topic.Parent.ConsumerConfig.ClientId = "global-client";

        var config = new KafkaListenerConfiguration(topic)
            .ConfigureConsumer(consumer => consumer.GroupId = "topic")
            .ExtendConsumerConfiguration(consumer => consumer.ClientId = "topic-client");

        ((IDelayedEndpointConfiguration)config).Apply();

        topic.ConsumerConfig.ShouldNotBeNull();
        topic.ConsumerConfig.BootstrapServers.ShouldBe("localhost:9092");
        topic.ConsumerConfig.GroupId.ShouldBe("topic");
        topic.ConsumerConfig.ClientId.ShouldBe("topic-client");
    }

    [Fact]
    public void extend_consumer_configuration_applies_new_configuration_last()
    {
        var topic = BuildTopic();
        topic.Parent.ConsumerConfig.GroupId = "parent-group";

        var config = new KafkaListenerConfiguration(topic)
            .ConfigureConsumer(consumer => consumer.GroupId = "topic-group")
            .ExtendConsumerConfiguration(consumer => consumer.GroupId = "extended-group");

        ((IDelayedEndpointConfiguration)config).Apply();

        topic.ConsumerConfig.ShouldNotBeNull();
        topic.ConsumerConfig.GroupId.ShouldBe("extended-group");
    }

    [Fact]
    public void extend_consumer_configuration_null_throws()
    {
        Should.Throw<ArgumentNullException>(() =>
            new KafkaListenerConfiguration(BuildTopic())
                .ExtendConsumerConfiguration(null!));
    }

    [Fact]
    public void begin_at_earliest_inherits_group_id_from_parent_transport()
    {
        var topic = BuildTopic();
        topic.Parent.ConsumerConfig.GroupId = "my-group";
        topic.Parent.ConsumerConfig.BootstrapServers = "localhost:9092";

        var config = new KafkaListenerConfiguration(topic)
            .BeginAtEarliest();
        ((IDelayedEndpointConfiguration)config).Apply();

        var effective = topic.GetEffectiveConsumerConfig();
        effective.GroupId.ShouldBe("my-group");
        effective.AutoOffsetReset.ShouldBe(AutoOffsetReset.Earliest);
        effective.BootstrapServers.ShouldBe("localhost:9092");
    }

    [Fact]
    public void begin_at_latest_inherits_group_id_from_parent_transport()
    {
        var topic = BuildTopic();
        topic.Parent.ConsumerConfig.GroupId = "my-group";
        topic.Parent.ConsumerConfig.BootstrapServers = "localhost:9092";

        var config = new KafkaListenerConfiguration(topic)
            .BeginAtLatest();
        ((IDelayedEndpointConfiguration)config).Apply();

        var effective = topic.GetEffectiveConsumerConfig();
        effective.GroupId.ShouldBe("my-group");
        effective.AutoOffsetReset.ShouldBe(AutoOffsetReset.Latest);
        effective.BootstrapServers.ShouldBe("localhost:9092");
    }

    [Fact]
    public void begin_at_earliest_does_not_override_explicitly_set_group_id()
    {
        var topic = BuildTopic();
        topic.Parent.ConsumerConfig.GroupId = "parent-group";

        var config = new KafkaListenerConfiguration(topic)
            .ConfigureConsumer(c => c.GroupId = "topic-group")
            .BeginAtEarliest();
        ((IDelayedEndpointConfiguration)config).Apply();

        var effective = topic.GetEffectiveConsumerConfig();
        effective.GroupId.ShouldBe("topic-group");
        effective.AutoOffsetReset.ShouldBe(AutoOffsetReset.Earliest);
    }
}

public class UseKafkaUsingNamedConnectionTests
{
    [Fact]
    public void registers_named_connection_source_that_reads_from_configuration()
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["ConnectionStrings:kafka"] = "broker1:9092,broker2:9092"
            })
            .Build();

        var options = new WolverineOptions();
        options.UseKafkaUsingNamedConnection("kafka");
        options.Services.AddSingleton<IConfiguration>(configuration);

        var provider = options.Services.BuildServiceProvider();
        var source = provider.GetRequiredService<KafkaNamedConnectionSource>();
        source.BootstrapServers.ShouldBe("broker1:9092,broker2:9092");
    }

    [Fact]
    public void throws_when_connection_string_is_missing()
    {
        var configuration = new ConfigurationBuilder().Build();

        var options = new WolverineOptions();
        options.UseKafkaUsingNamedConnection("kafka");
        options.Services.AddSingleton<IConfiguration>(configuration);

        var provider = options.Services.BuildServiceProvider();
        Should.Throw<InvalidOperationException>(() => provider.GetRequiredService<KafkaNamedConnectionSource>())
            .Message.ShouldContain("kafka");
    }

    [Fact]
    public void returns_kafka_transport_expression()
    {
        var options = new WolverineOptions();
        var expression = options.UseKafkaUsingNamedConnection("kafka");
        expression.ShouldNotBeNull();
    }

    [Fact]
    public void disables_automatic_failure_acks()
    {
        var options = new WolverineOptions();
        options.UseKafkaUsingNamedConnection("kafka");
        options.EnableAutomaticFailureAcks.ShouldBeFalse();
    }
}
