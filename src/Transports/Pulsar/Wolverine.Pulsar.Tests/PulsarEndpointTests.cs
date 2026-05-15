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
}