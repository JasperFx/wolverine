using Dekaf.Security.Sasl;
using Dekaf.Consumer;
using Dekaf.Producer;
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

        var capturedSpec = new KafkaTopicSpecification { Name = "topic-a" };
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

        var specA = new KafkaTopicSpecification { Name = "topic-a" };
        group.SpecificationConfig!("topic-a", specA);
        specA.NumPartitions.ShouldBe(6);

        var specB = new KafkaTopicSpecification { Name = "topic-b" };
        group.SpecificationConfig!("topic-b", specB);
        specB.NumPartitions.ShouldBe(24);
    }

    [Fact]
    public void topic_creation_sets_func_on_group()
    {
        var group = BuildGroup("topic-a", "topic-b");
        Func<Dekaf.Admin.IAdminClient, string, Task> func = (_, _) => Task.CompletedTask;

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
                .Specification((Action<KafkaTopicSpecification>)null!));
    }

    [Fact]
    public void specification_per_topic_null_throws()
    {
        var group = BuildGroup("topic-a");
        Should.Throw<ArgumentNullException>(() =>
            new KafkaTopicGroupListenerConfiguration(group)
                .Specification((Action<string, KafkaTopicSpecification>)null!));
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

    [Theory]
    [InlineData(Acks.None)]
    [InlineData(Acks.Leader)]
    public void acks_below_all_need_idempotence_turned_off(Acks acks)
    {
        Should.Throw<InvalidOperationException>(() =>
            new KafkaProducerOptions { Acks = acks }.AssertAcksAllowed("test"));

        new KafkaProducerOptions { Acks = acks, EnableIdempotence = false }.AssertAcksAllowed("test");
    }

    [Fact]
    public async Task a_disposed_lazy_producer_refuses_to_build_another()
    {
        var transport = new KafkaTransport();
        var producer = new LazyKafkaProducer(transport, transport.ProducerConfig);

        await producer.DisposeAsync();
        await producer.DisposeAsync();

        await Should.ThrowAsync<ObjectDisposedException>(async () => await producer.GetAsync());
    }

    [Fact]
    public void topics_with_the_same_producer_options_share_one_producer()
    {
        var transport = new KafkaTransport { ProducerConfig = { BootstrapServers = "localhost:9092" } };

        var one = transport.ProducerFor(transport.Topics["one"]);
        transport.ProducerFor(transport.Topics["two"]).ShouldBeSameAs(one);
        transport.ProducerFor(transport.Topics[transport.DeadLetterQueueTopicName]).ShouldBeSameAs(one);
        transport.Producers.Count.ShouldBe(1);
    }

    [Fact]
    public void a_topic_with_its_own_producer_options_gets_its_own_producer()
    {
        var transport = new KafkaTransport { ProducerConfig = { BootstrapServers = "localhost:9092" } };
        var lingering = transport.Topics["lingering"];
        lingering.ProducerConfig = new KafkaProducerOptions { Linger = TimeSpan.FromMilliseconds(50) };

        transport.ProducerFor(lingering).ShouldNotBeSameAs(transport.ProducerFor(transport.Topics["plain"]));

        // It still inherits the cluster's connection settings
        transport.ProducerFor(lingering).Config.BootstrapServers.ShouldBe("localhost:9092");
    }

    [Fact]
    public async Task disposing_the_transport_disposes_its_tenants_producers()
    {
        var transport = new KafkaTransport { ProducerConfig = { BootstrapServers = "localhost:9092" } };
        var tenant = new KafkaTenant("red");
        tenant.Transport.ProducerConfig = new KafkaProducerOptions { BootstrapServers = "localhost:9093" };
        transport.Tenants[tenant.TenantId] = tenant;

        var topic = transport.Topics["one"];
        var tenantProducer = tenant.Transport.ProducerFor(topic);
        tenantProducer.ShouldNotBeSameAs(transport.ProducerFor(topic));
        tenantProducer.Config.BootstrapServers.ShouldBe("localhost:9093");

        await transport.DisposeAsync();

        await Should.ThrowAsync<ObjectDisposedException>(async () => await tenantProducer.GetAsync());
    }

    [Fact]
    public void acks_all_is_allowed_with_idempotence()
    {
        new KafkaProducerOptions { Acks = Acks.All }.AssertAcksAllowed("test");
    }

    [Theory]
    [InlineData(SaslMechanism.Gssapi)]
    [InlineData(SaslMechanism.OAuthBearer)]
    [InlineData(SaslMechanism.AwsMskIam)]
    public void sasl_mechanisms_that_need_more_than_a_password_are_refused(SaslMechanism mechanism)
    {
        Should.Throw<InvalidOperationException>(() =>
            new KafkaConsumerOptions { SaslMechanism = mechanism }.AssertSaslSupported("test"));
    }

    [Theory]
    [InlineData(SaslMechanism.None)]
    [InlineData(SaslMechanism.Plain)]
    [InlineData(SaslMechanism.ScramSha256)]
    [InlineData(SaslMechanism.ScramSha512)]
    public void password_sasl_mechanisms_are_allowed(SaslMechanism mechanism)
    {
        new KafkaConsumerOptions { SaslMechanism = mechanism }.AssertSaslSupported("test");
    }

    [Fact]
    public void by_duration_offset_reset_needs_a_duration()
    {
        var options = new KafkaConsumerOptions { BootstrapServers = "localhost:9092", AutoOffsetReset = AutoOffsetReset.ByDuration };

        Should.Throw<InvalidOperationException>(() => Dekaf.Kafka.CreateConsumer<string, byte[]>().ApplyOptions(options));

        options.AutoOffsetResetDuration = TimeSpan.FromHours(1);
        Dekaf.Kafka.CreateConsumer<string, byte[]>().ApplyOptions(options);
    }

    [Fact]
    public void topic_consumer_options_inherit_the_offset_reset_duration()
    {
        var transport = new KafkaTransport();
        transport.ConsumerConfig.AutoOffsetResetDuration = TimeSpan.FromHours(1);
        transport.ConsumerConfig.OffsetCommitMode = OffsetCommitMode.Manual;

        var topic = transport.Topics["one"];
        topic.ConsumerConfig = new KafkaConsumerOptions();
        topic.ConsumerConfig.InheritDefaultsFrom(transport.ConsumerConfig);

        topic.ConsumerConfig.AutoOffsetResetDuration.ShouldBe(TimeSpan.FromHours(1));
        topic.ConsumerConfig.OffsetCommitMode.ShouldBe(OffsetCommitMode.Manual);
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

    // The methods below (BeginAtEarliest/BeginAtLatest/UseReadCommitted/
    // UseStaticMembership/TailFromLatest) all build a fresh per-topic ConsumerConfig containing only the
    // one property they set, the same as BeginAtEarliest() did for GroupId before it was fixed above.
    // GetEffectiveConsumerConfig() previously only backfilled BootstrapServers/GroupId from the parent,
    // silently dropping UseTls/SaslMechanism/SaslUsername/SaslPassword -- a listener using any
    // of these methods without a subsequent ExtendConsumerConfiguration() call would connect to a
    // SASL_SSL broker without credentials and be disconnected during the initial handshake.

    [Fact]
    public void begin_at_earliest_inherits_tls_and_sasl_settings_from_parent_transport()
    {
        var topic = BuildTopic();
        topic.Parent.ConsumerConfig.UseTls = true;
        topic.Parent.ConsumerConfig.SaslMechanism = SaslMechanism.Plain;
        topic.Parent.ConsumerConfig.SaslUsername = "api-key";
        topic.Parent.ConsumerConfig.SaslPassword = "api-secret";

        var config = new KafkaListenerConfiguration(topic)
            .BeginAtEarliest();
        ((IDelayedEndpointConfiguration)config).Apply();

        var effective = topic.GetEffectiveConsumerConfig();
        effective.UseTls.ShouldBe(true);
        effective.SaslMechanism.ShouldBe(SaslMechanism.Plain);
        effective.SaslUsername.ShouldBe("api-key");
        effective.SaslPassword.ShouldBe("api-secret");
    }

    [Fact]
    public void begin_at_latest_inherits_tls_and_sasl_settings_from_parent_transport()
    {
        var topic = BuildTopic();
        topic.Parent.ConsumerConfig.UseTls = true;
        topic.Parent.ConsumerConfig.SaslMechanism = SaslMechanism.Plain;
        topic.Parent.ConsumerConfig.SaslUsername = "api-key";
        topic.Parent.ConsumerConfig.SaslPassword = "api-secret";

        var config = new KafkaListenerConfiguration(topic)
            .BeginAtLatest();
        ((IDelayedEndpointConfiguration)config).Apply();

        var effective = topic.GetEffectiveConsumerConfig();
        effective.UseTls.ShouldBe(true);
        effective.SaslMechanism.ShouldBe(SaslMechanism.Plain);
        effective.SaslUsername.ShouldBe("api-key");
        effective.SaslPassword.ShouldBe("api-secret");
    }

    [Fact]
    public void use_read_committed_inherits_tls_and_sasl_settings_from_parent_transport()
    {
        var topic = BuildTopic();
        topic.Parent.ConsumerConfig.UseTls = true;
        topic.Parent.ConsumerConfig.SaslUsername = "api-key";
        topic.Parent.ConsumerConfig.SaslPassword = "api-secret";

        var config = new KafkaListenerConfiguration(topic)
            .UseReadCommitted();
        ((IDelayedEndpointConfiguration)config).Apply();

        var effective = topic.GetEffectiveConsumerConfig();
        effective.UseTls.ShouldBe(true);
        effective.SaslUsername.ShouldBe("api-key");
        effective.SaslPassword.ShouldBe("api-secret");
    }

    [Fact]
    public void use_static_membership_inherits_tls_and_sasl_settings_from_parent_transport()
    {
        var topic = BuildTopic();
        topic.Parent.ConsumerConfig.UseTls = true;
        topic.Parent.ConsumerConfig.SaslUsername = "api-key";
        topic.Parent.ConsumerConfig.SaslPassword = "api-secret";

        var config = new KafkaListenerConfiguration(topic)
            .UseStaticMembership("node-1");
        ((IDelayedEndpointConfiguration)config).Apply();

        var effective = topic.GetEffectiveConsumerConfig();
        effective.UseTls.ShouldBe(true);
        effective.SaslUsername.ShouldBe("api-key");
        effective.SaslPassword.ShouldBe("api-secret");
    }

    [Fact]
    public void tail_from_latest_inherits_tls_and_sasl_settings_from_parent_transport()
    {
        var topic = BuildTopic();
        topic.Parent.ConsumerConfig.UseTls = true;
        topic.Parent.ConsumerConfig.SaslUsername = "api-key";
        topic.Parent.ConsumerConfig.SaslPassword = "api-secret";

        var config = new KafkaListenerConfiguration(topic)
            .TailFromLatest();
        ((IDelayedEndpointConfiguration)config).Apply();

        var effective = topic.GetEffectiveConsumerConfig();
        effective.UseTls.ShouldBe(true);
        effective.SaslUsername.ShouldBe("api-key");
        effective.SaslPassword.ShouldBe("api-secret");
    }

    [Fact]
    public void begin_at_earliest_does_not_override_explicitly_set_sasl_username()
    {
        var topic = BuildTopic();
        topic.Parent.ConsumerConfig.SaslUsername = "parent-key";

        var config = new KafkaListenerConfiguration(topic)
            .ConfigureConsumer(c => c.SaslUsername = "topic-key")
            .BeginAtEarliest();
        ((IDelayedEndpointConfiguration)config).Apply();

        var effective = topic.GetEffectiveConsumerConfig();
        effective.SaslUsername.ShouldBe("topic-key");
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

        // Still an InvalidOperationException, which is what this asserted before GH-4527 replaced the bare
        // throw -- MissingNamedConnectionStringsException derives from it precisely so this keeps working.
        var ex = Should.Throw<MissingNamedConnectionStringsException>(() =>
            provider.GetRequiredService<KafkaNamedConnectionSource>());

        ex.ShouldBeAssignableTo<InvalidOperationException>();
        ex.Message.ShouldContain("kafka");

        // ...and it carries the richer detail the aggregated startup check produces
        ex.Missing.Single().Name.ShouldBe("kafka");
        ex.Message.ShouldContain("builder.AddKafka(\"kafka\")");
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
