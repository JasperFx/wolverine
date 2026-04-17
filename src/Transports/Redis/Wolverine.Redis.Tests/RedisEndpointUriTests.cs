using Shouldly;
using Xunit;

namespace Wolverine.Redis.Tests;

public class RedisEndpointUriTests
{
    [Fact]
    public void stream_uri_has_expected_shape_with_default_database()
    {
        RedisEndpointUri.Stream("orders")
            .ShouldBe(new Uri("redis://stream/0/orders"));
    }

    [Fact]
    public void stream_uri_has_expected_shape_with_explicit_database()
    {
        RedisEndpointUri.Stream("orders", databaseId: 3)
            .ShouldBe(new Uri("redis://stream/3/orders"));
    }

    [Fact]
    public void stream_uri_includes_consumer_group_query_parameter()
    {
        RedisEndpointUri.Stream("orders", 3, "order-processors")
            .ShouldBe(new Uri("redis://stream/3/orders?consumerGroup=order-processors"));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void stream_rejects_invalid_key(string? key)
    {
        Should.Throw<ArgumentException>(() => RedisEndpointUri.Stream(key!));
    }

    [Fact]
    public void stream_rejects_negative_database_id()
    {
        Should.Throw<ArgumentOutOfRangeException>(() => RedisEndpointUri.Stream("orders", databaseId: -1));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void stream_with_consumer_group_rejects_invalid_group(string? group)
    {
        Should.Throw<ArgumentException>(() => RedisEndpointUri.Stream("orders", 0, group!));
    }

    [Fact]
    public void stream_uri_roundtrips_through_parser()
    {
        var uri = RedisEndpointUri.Stream("orders", databaseId: 3);
        var transport = new Wolverine.Redis.Internal.RedisTransport();
        var endpoint = transport.GetOrCreateEndpoint(uri);
        endpoint.Uri.ShouldBe(uri);
    }

    [Fact]
    public void consumer_group_uri_roundtrips_through_parser()
    {
        var uri = RedisEndpointUri.Stream("orders", databaseId: 3, consumerGroup: "order-processors");
        var transport = new Wolverine.Redis.Internal.RedisTransport();
        var endpoint = (Wolverine.Redis.Internal.RedisStreamEndpoint)transport.GetOrCreateEndpoint(uri);
        endpoint.ConsumerGroup.ShouldBe("order-processors");
    }
}
