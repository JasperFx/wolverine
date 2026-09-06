using Shouldly;
using Wolverine.Configuration;
using Xunit;

namespace Wolverine.AzureServiceBus.Tests;

public class configuring_prefetch_count
{
    [Fact]
    public void prefetch_is_disabled_by_default()
    {
        var transport = new AzureServiceBusTransport();

        transport.PrefetchCount.ShouldBe(0);
        transport.Queues["incoming"].PrefetchCount.ShouldBe(0);
    }

    // GH-4331 gave these two modes a computed default of one receive batch on the reasoning that
    // they settle early and so do not hold a lock across handler execution. The rig lane measured
    // no benefit, and prefetch is not free -- a buffered message ages against its lock with no
    // Envelope and therefore no lease renewal -- so the default went back to zero. This test is the
    // guard on that reversal, not a claim that prefetch is useless.
    [Theory]
    [InlineData(EndpointMode.BufferedInMemory)]
    [InlineData(EndpointMode.Durable)]
    [InlineData(EndpointMode.Inline)]
    public void no_mode_other_than_native_ack_computes_a_prefetch_default(EndpointMode mode)
    {
        var queue = new AzureServiceBusTransport().Queues["incoming"];
        queue.Mode = mode;

        queue.PrefetchCount.ShouldBe(0);
    }

    [Fact]
    public void fluent_configuration_flows_to_the_queue_endpoint()
    {
        var transport = new AzureServiceBusTransport();
        var queue = transport.Queues["incoming"];

        var configuration = new AzureServiceBusQueueListenerConfiguration(queue);
        configuration.PrefetchCount(50).ShouldBeSameAs(configuration);

        // Apply the delayed configuration as Wolverine would at bootstrapping time
        ((IDelayedEndpointConfiguration)configuration).Apply();

        queue.PrefetchCount.ShouldBe(50);
    }

    [Fact]
    public void fluent_configuration_flows_to_the_subscription_endpoint()
    {
        var transport = new AzureServiceBusTransport();
        var topic = transport.Topics["topic1"];
        var subscription = topic.FindOrCreateSubscription("sub1");

        var configuration = new AzureServiceBusSubscriptionListenerConfiguration(subscription);
        configuration.PrefetchCount(25).ShouldBeSameAs(configuration);

        ((IDelayedEndpointConfiguration)configuration).Apply();

        subscription.PrefetchCount.ShouldBe(25);
    }

    [Fact]
    public void transport_wide_default_is_inherited_by_endpoints()
    {
        var transport = new AzureServiceBusTransport();
        var configuration = new AzureServiceBusConfiguration(transport, new WolverineOptions());

        configuration.PrefetchCount(100).ShouldBeSameAs(configuration);

        transport.PrefetchCount.ShouldBe(100);
        transport.Queues["incoming"].PrefetchCount.ShouldBe(100);
        transport.Topics["topic1"].FindOrCreateSubscription("sub1").PrefetchCount.ShouldBe(100);
    }

    [Fact]
    public void endpoint_override_wins_over_the_transport_wide_default()
    {
        var transport = new AzureServiceBusTransport();
        transport.PrefetchCount = 100;

        var queue = transport.Queues["incoming"];
        queue.PrefetchCount = 10;

        queue.PrefetchCount.ShouldBe(10);

        // Other endpoints still see the transport default
        transport.Queues["other"].PrefetchCount.ShouldBe(100);
    }

    [Fact]
    public void negative_values_are_rejected()
    {
        var transport = new AzureServiceBusTransport();
        var queue = transport.Queues["incoming"];
        var listenerConfiguration = new AzureServiceBusQueueListenerConfiguration(queue);
        var subscription = transport.Topics["topic1"].FindOrCreateSubscription("sub1");
        var subscriptionConfiguration = new AzureServiceBusSubscriptionListenerConfiguration(subscription);

        Should.Throw<ArgumentOutOfRangeException>(() => transport.PrefetchCount = -1);
        Should.Throw<ArgumentOutOfRangeException>(() => queue.PrefetchCount = -1);
        Should.Throw<ArgumentOutOfRangeException>(() => listenerConfiguration.PrefetchCount(-1));
        Should.Throw<ArgumentOutOfRangeException>(() => subscriptionConfiguration.PrefetchCount(-1));
    }

    [Fact]
    public void build_receiver_options_carries_the_endpoint_prefetch_count()
    {
        var transport = new AzureServiceBusTransport();
        var queue = transport.Queues["incoming"];
        queue.PrefetchCount = 50;

        var options = AzureServiceBusTransport.BuildReceiverOptions(queue);

        options.PrefetchCount.ShouldBe(50);
    }

    [Fact]
    public void build_receiver_options_carries_the_transport_default()
    {
        var transport = new AzureServiceBusTransport();
        transport.PrefetchCount = 75;

        var options = AzureServiceBusTransport.BuildReceiverOptions(transport.Queues["incoming"]);

        options.PrefetchCount.ShouldBe(75);
    }

    [Fact]
    public void build_session_receiver_options_carries_the_endpoint_prefetch_count()
    {
        var transport = new AzureServiceBusTransport();
        var subscription = transport.Topics["topic1"].FindOrCreateSubscription("sub1");
        subscription.PrefetchCount = 30;

        var options = AzureServiceBusTransport.BuildSessionReceiverOptions(subscription);

        options.PrefetchCount.ShouldBe(30);
    }

    [Fact]
    public void build_processor_options_carries_the_endpoint_prefetch_count()
    {
        var transport = new AzureServiceBusTransport();
        var queue = transport.Queues["incoming"];
        queue.PrefetchCount = 40;

        var options = AzureServiceBusTransport.BuildProcessorOptions(queue);

        options.PrefetchCount.ShouldBe(40);
    }

    [Fact]
    public void configure_processor_can_still_override_the_prefetch_count_for_inline_listeners()
    {
        var transport = new AzureServiceBusTransport();
        var queue = transport.Queues["incoming"];
        queue.PrefetchCount = 40;
        queue.ConfigureProcessor = o => o.PrefetchCount = 5;

        var options = AzureServiceBusTransport.BuildProcessorOptions(queue);

        options.PrefetchCount.ShouldBe(5);
    }
}
