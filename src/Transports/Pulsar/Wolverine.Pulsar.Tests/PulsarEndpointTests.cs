using JasperFx.Core;
using Shouldly;
using Xunit;

namespace Wolverine.Pulsar.Tests;

public class PulsarEndpointTests
{
    [Fact]
    public void reject_uri_that_is_not_pulsar()
    {
        Should.Throw<InvalidPulsarUriException>(() => { new PulsarEndpoint("tcp://server:5000".ToUri(), null!); });
    }

    [Fact]
    public void reject_uri_with_wrong_number_of_segments()
    {
        Should.Throw<InvalidPulsarUriException>(() =>
        {
            new PulsarEndpoint("pulsar://persistent/public/default".ToUri(), null!);
        });

        Should.Throw<InvalidPulsarUriException>(() =>
        {
            new PulsarEndpoint("pulsar://persistent/public/default/topica/more".ToUri(), null!);
        });
    }

    [Fact]
    public void reject_uri_with_invalid_persistent_flag()
    {
        Should.Throw<InvalidPulsarUriException>(() =>
        {
            new PulsarEndpoint("pulsar://wrong/public/default/aaa".ToUri(), null!);
        });
    }

    [Fact]
    public void parse_acceptable_persistent_uri()
    {
        var endpoint = new PulsarEndpoint("pulsar://persistent/public/default/aaa".ToUri(), null!);
        endpoint.Persistence.ShouldBe(PulsarEndpoint.Persistent);
        endpoint.Tenant.ShouldBe("public");
        endpoint.Namespace.ShouldBe("default");
        endpoint.TopicName.ShouldBe("aaa");
    }

    [Fact]
    public void parse_acceptable_nonpersistent_uri()
    {
        var endpoint = new PulsarEndpoint("pulsar://non-persistent/public/default/aaa".ToUri(), null!);
        endpoint.Persistence.ShouldBe(PulsarEndpoint.NonPersistent);
        endpoint.Tenant.ShouldBe("public");
        endpoint.Namespace.ShouldBe("default");
        endpoint.TopicName.ShouldBe("aaa");
    }

    [Fact]
    public void native_topic_path_all_props_and_persistent()
    {
        var uri = PulsarEndpoint.NativeTopicPath(true, "public", "default", "aaa");
        uri.ShouldBe(new Uri("persistent://public/default/aaa"));
    }

    [Fact]
    public void native_topic_path_all_props_and_non_persistent()
    {
        var uri = PulsarEndpoint.NativeTopicPath(false, "public", "default", "aaa");
        uri.ShouldBe(new Uri("non-persistent://public/default/aaa"));
    }

    [Fact]
    public void uri_for_topic_string()
    {
        var topicPath = "persistent://t1/ns1/aaa";
        var uri = PulsarEndpointUri.Topic(topicPath);
        var endpoint = new PulsarEndpoint(uri, null!);

        endpoint.Persistence.ShouldBe(PulsarEndpoint.Persistent);
        endpoint.Namespace.ShouldBe("ns1");
        endpoint.Tenant.ShouldBe("t1");
        endpoint.TopicName.ShouldBe("aaa");
    }

    [Fact]
    public void uri_for_topic_string_handles_non_persistent_scheme()
    {
        var uri = PulsarEndpointUri.Topic("non-persistent://t1/ns1/aaa");
        var endpoint = new PulsarEndpoint(uri, null!);

        endpoint.Persistence.ShouldBe(PulsarEndpoint.NonPersistent);
        endpoint.Tenant.ShouldBe("t1");
        endpoint.Namespace.ShouldBe("ns1");
        endpoint.TopicName.ShouldBe("aaa");
    }

    [Fact]
    public void uri_for_topic_string_preserves_realistic_topic_names()
    {
        var uri = PulsarEndpointUri.Topic("persistent://my-tenant/my-namespace/order.created.v2");
        var endpoint = new PulsarEndpoint(uri, null!);

        endpoint.Persistence.ShouldBe(PulsarEndpoint.Persistent);
        endpoint.Tenant.ShouldBe("my-tenant");
        endpoint.Namespace.ShouldBe("my-namespace");
        endpoint.TopicName.ShouldBe("order.created.v2");
    }

    [Fact]
    public void native_topic_path_produces_retry_suffix_used_by_pulsar_listener()
    {
        // Mirrors the call pattern in PulsarListener.getRetryLetterTopicUri:
        // PulsarEndpoint.NativeTopicPath(isPersistent, tenant, namespace, "{topic}-RETRY").
        var uri = PulsarEndpoint.NativeTopicPath(true, "public", "default", "orders-RETRY");
        uri.ShouldBe(new Uri("persistent://public/default/orders-RETRY"));
    }

    [Fact]
    public void native_topic_path_produces_dlq_suffix_used_by_pulsar_listener()
    {
        // Mirrors the call pattern in PulsarListener.getDeadLetteredTopicUri:
        // PulsarEndpoint.NativeTopicPath(isPersistent, tenant, namespace, "{topic}-DLQ").
        var uri = PulsarEndpoint.NativeTopicPath(true, "public", "default", "orders-DLQ");
        uri.ShouldBe(new Uri("persistent://public/default/orders-DLQ"));
    }

    [Fact]
    public void effective_dead_letter_topic_falls_back_to_transport_default()
    {
        var transport = new PulsarTransport
        {
            DeadLetterTopic = new DeadLetterTopic("transport-default-dlq", DeadLetterTopicMode.Native)
        };

        var endpoint = transport["pulsar://persistent/public/default/orders".ToUri()];

        // No per-endpoint override -> transport-wide default is used.
        endpoint.EffectiveDeadLetterTopic!.TopicName.ShouldBe("transport-default-dlq");
    }

    [Fact]
    public void effective_dead_letter_topic_prefers_per_endpoint_override()
    {
        var transport = new PulsarTransport
        {
            DeadLetterTopic = new DeadLetterTopic("transport-default-dlq", DeadLetterTopicMode.Native)
        };

        var endpoint = transport["pulsar://persistent/public/default/orders".ToUri()];
        endpoint.DeadLetterTopic = new DeadLetterTopic("endpoint-dlq", DeadLetterTopicMode.Native);

        // Per-endpoint configuration always wins over the transport default.
        endpoint.EffectiveDeadLetterTopic!.TopicName.ShouldBe("endpoint-dlq");
    }

    [Fact]
    public void effective_retry_letter_topic_prefers_per_endpoint_override()
    {
        var transportDefault = new RetryLetterTopic([1.Seconds()]);
        var endpointOverride = new RetryLetterTopic([2.Seconds()]);

        var transport = new PulsarTransport { RetryLetterTopic = transportDefault };
        var endpoint = transport["pulsar://persistent/public/default/orders".ToUri()];

        endpoint.EffectiveRetryLetterTopic.ShouldBe(transportDefault);

        endpoint.RetryLetterTopic = endpointOverride;
        endpoint.EffectiveRetryLetterTopic.ShouldBe(endpointOverride);
    }

    [Theory]
    [InlineData(DeadLetterTopicMode.Native)]
    [InlineData(DeadLetterTopicMode.WolverineStorage)]
    public void try_build_dead_letter_sender_is_always_false(DeadLetterTopicMode mode)
    {
        var transport = new PulsarTransport();
        var endpoint = transport["pulsar://persistent/public/default/orders".ToUri()];
        endpoint.DeadLetterTopic = new DeadLetterTopic(mode);

        // Pulsar never uses an endpoint-level DLQ sender: native dead-lettering is owned by the
        // listener (ISupportDeadLetterQueue, with reconsume metadata) and WolverineStorage by the
        // durable store. Returning a sender here would hijack the native path. See #3186.
        endpoint.TryBuildDeadLetterSender(null!, out var sender).ShouldBeFalse();
        sender.ShouldBeNull();
    }
}