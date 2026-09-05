using Shouldly;
using Wolverine.Configuration;
using Xunit;

namespace Wolverine.AzureServiceBus.Tests;

public class configuring_prefetch_count
{
    [Fact]
    public void the_transport_wide_default_is_still_zero()
    {
        // The transport-level value is the explicit opt-in knob and is unchanged; what varies is
        // what an endpoint computes when nothing was configured (see the per-mode tests below)
        new AzureServiceBusTransport().PrefetchCount.ShouldBe(0);
    }

    // GH-4331: Buffered and Durable settle at receipt / right after the inbox insert, so a modest
    // prefetch buffer does not age against a lock across handler execution. Inline does run the
    // handler before settling, so it keeps zero.
    [Theory]
    [InlineData(EndpointMode.BufferedInMemory)]
    [InlineData(EndpointMode.Durable)]
    public void settle_early_modes_prefetch_one_receive_batch_by_default(EndpointMode mode)
    {
        var queue = new AzureServiceBusTransport().Queues["incoming"];
        queue.Mode = mode;

        queue.PrefetchCount.ShouldBe(queue.MaximumMessagesToReceive);
    }

    [Fact]
    public void inline_still_disables_prefetch_by_default()
    {
        var queue = new AzureServiceBusTransport().Queues["incoming"];
        queue.Mode = EndpointMode.Inline;

        queue.PrefetchCount.ShouldBe(0);
    }

    [Fact]
    public void an_explicit_transport_wide_zero_still_wins_over_the_computed_default()
    {
        var transport = new AzureServiceBusTransport();
        transport.PrefetchCount = 0;

        var queue = transport.Queues["incoming"];
        queue.Mode = EndpointMode.Durable;

        // Setting it explicitly to 0 is a deliberate "no prefetch", not an absence of configuration
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
