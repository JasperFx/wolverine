using Google.Cloud.PubSub.V1;
using Shouldly;
using Xunit;

namespace Wolverine.Pubsub.Tests;

public class PubsubConfigurationTests
{
    [Fact]
    public void configure_publisher_api_client_sets_callback_on_transport()
    {
        var transport = new PubsubTransport("test");
        var config = new PubsubConfiguration(transport, new WolverineOptions());
        var called = false;

        config.ConfigurePublisherApiClient(_ => called = true);

        transport.ConfigurePublisherApiBuilder.ShouldNotBeNull();
        transport.ConfigurePublisherApiBuilder.Invoke(new PublisherServiceApiClientBuilder());
        called.ShouldBeTrue();
    }

    [Fact]
    public void configure_subscriber_api_client_sets_callback_on_transport()
    {
        var transport = new PubsubTransport("test");
        var config = new PubsubConfiguration(transport, new WolverineOptions());
        var called = false;

        config.ConfigureSubscriberApiClient(_ => called = true);

        transport.ConfigureSubscriberApiBuilder.ShouldNotBeNull();
        transport.ConfigureSubscriberApiBuilder.Invoke(new SubscriberServiceApiClientBuilder());
        called.ShouldBeTrue();
    }

    [Fact]
    public void configure_subscriber_client_sets_callback_on_transport()
    {
        var transport = new PubsubTransport("test");
        var config = new PubsubConfiguration(transport, new WolverineOptions());
        var called = false;

        config.ConfigureSubscriberClient(_ => called = true);

        transport.ConfigureSubscriberClientBuilder.ShouldNotBeNull();
        transport.ConfigureSubscriberClientBuilder.Invoke(new SubscriberClientBuilder());
        called.ShouldBeTrue();
    }

    [Fact]
    public void multiple_configure_publisher_api_client_calls_compose_in_order()
    {
        var transport = new PubsubTransport("test");
        var config = new PubsubConfiguration(transport, new WolverineOptions());
        var order = new List<int>();

        config.ConfigurePublisherApiClient(_ => order.Add(1));
        config.ConfigurePublisherApiClient(_ => order.Add(2));

        transport.ConfigurePublisherApiBuilder!.Invoke(new PublisherServiceApiClientBuilder());
        order.ShouldBe([1, 2]);
    }

    [Fact]
    public void multiple_configure_subscriber_client_calls_compose_in_order()
    {
        var transport = new PubsubTransport("test");
        var config = new PubsubConfiguration(transport, new WolverineOptions());
        var order = new List<int>();

        config.ConfigureSubscriberClient(_ => order.Add(1));
        config.ConfigureSubscriberClient(_ => order.Add(2));

        transport.ConfigureSubscriberClientBuilder!.Invoke(new SubscriberClientBuilder());
        order.ShouldBe([1, 2]);
    }
}