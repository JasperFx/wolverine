using Shouldly;
using Xunit;

namespace Wolverine.Pulsar.Tests;

public class PulsarEndpointUriTests
{
    [Fact]
    public void persistent_topic_returns_wolverine_endpoint_uri()
    {
        PulsarEndpointUri.PersistentTopic("public", "default", "orders")
            .ShouldBe(new Uri("pulsar://persistent/public/default/orders"));
    }

    [Fact]
    public void non_persistent_topic_returns_wolverine_endpoint_uri()
    {
        PulsarEndpointUri.NonPersistentTopic("public", "default", "orders")
            .ShouldBe(new Uri("pulsar://non-persistent/public/default/orders"));
    }

    [Fact]
    public void topic_with_persistent_flag_true_matches_persistent_topic()
    {
        PulsarEndpointUri.Topic("public", "default", "orders", persistent: true)
            .ShouldBe(new Uri("pulsar://persistent/public/default/orders"));
    }

    [Fact]
    public void topic_with_persistent_flag_false_matches_non_persistent_topic()
    {
        PulsarEndpointUri.Topic("public", "default", "orders", persistent: false)
            .ShouldBe(new Uri("pulsar://non-persistent/public/default/orders"));
    }

    [Fact]
    public void topic_from_pulsar_native_path_returns_wolverine_endpoint_uri()
    {
        PulsarEndpointUri.Topic("persistent://t1/ns1/aaa")
            .ShouldBe(new Uri("pulsar://persistent/t1/ns1/aaa"));
    }

    [Fact]
    public void topic_from_non_persistent_native_path_returns_wolverine_endpoint_uri()
    {
        PulsarEndpointUri.Topic("non-persistent://t1/ns1/aaa")
            .ShouldBe(new Uri("pulsar://non-persistent/t1/ns1/aaa"));
    }

    [Theory]
    [InlineData(null, "ns", "topic")]
    [InlineData("", "ns", "topic")]
    [InlineData("   ", "ns", "topic")]
    [InlineData("tenant", null, "topic")]
    [InlineData("tenant", "", "topic")]
    [InlineData("tenant", "   ", "topic")]
    [InlineData("tenant", "ns", null)]
    [InlineData("tenant", "ns", "")]
    [InlineData("tenant", "ns", "   ")]
    public void persistent_topic_rejects_invalid_args(string? tenant, string? ns, string? topic)
    {
        Should.Throw<ArgumentException>(() => PulsarEndpointUri.PersistentTopic(tenant!, ns!, topic!));
    }

    [Theory]
    [InlineData(null, "ns", "topic")]
    [InlineData("", "ns", "topic")]
    [InlineData("tenant", null, "topic")]
    [InlineData("tenant", "ns", null)]
    public void non_persistent_topic_rejects_invalid_args(string? tenant, string? ns, string? topic)
    {
        Should.Throw<ArgumentException>(() => PulsarEndpointUri.NonPersistentTopic(tenant!, ns!, topic!));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void topic_from_path_rejects_invalid_path(string? path)
    {
        Should.Throw<ArgumentException>(() => PulsarEndpointUri.Topic(path!));
    }

    [Theory]
    [InlineData("pulsar://persistent/t1/ns1/aaa")] // already a Wolverine endpoint URI
    [InlineData("http://example.com/ns/topic")]     // unsupported scheme
    [InlineData("ftp://server/ns/topic")]           // unsupported scheme
    [InlineData("tcp://host:5000/a/b")]             // unsupported scheme
    public void topic_from_path_rejects_unsupported_scheme(string path)
    {
        Should.Throw<ArgumentException>(() => PulsarEndpointUri.Topic(path));
    }

    [Theory]
    [InlineData("persistent://tenant")]                  // only tenant, missing ns and topic
    [InlineData("persistent://tenant/ns")]                // missing topic
    [InlineData("persistent://tenant/ns/topic/extra")]    // extra segment
    [InlineData("non-persistent://tenant/ns/topic/extra")] // extra segment, non-persistent
    public void topic_from_path_rejects_wrong_segment_count(string path)
    {
        Should.Throw<ArgumentException>(() => PulsarEndpointUri.Topic(path));
    }

    [Fact]
    public void persistent_topic_uri_roundtrips_through_parser()
    {
        var uri = PulsarEndpointUri.PersistentTopic("public", "default", "orders");
        var endpoint = new PulsarEndpoint(uri, null!);
        endpoint.Persistence.ShouldBe(PulsarEndpoint.Persistent);
        endpoint.Tenant.ShouldBe("public");
        endpoint.Namespace.ShouldBe("default");
        endpoint.TopicName.ShouldBe("orders");
    }

    [Fact]
    public void non_persistent_topic_uri_roundtrips_through_parser()
    {
        var uri = PulsarEndpointUri.NonPersistentTopic("public", "default", "orders");
        var endpoint = new PulsarEndpoint(uri, null!);
        endpoint.Persistence.ShouldBe(PulsarEndpoint.NonPersistent);
        endpoint.Tenant.ShouldBe("public");
        endpoint.Namespace.ShouldBe("default");
        endpoint.TopicName.ShouldBe("orders");
    }
}
