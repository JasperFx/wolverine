using Shouldly;
using Wolverine.AzureServiceBus.Internal;
using Wolverine.Configuration;
using Xunit;

namespace Wolverine.AzureServiceBus.Tests;

/// <summary>
/// GH-3494 (AO3). Wolverine never set MaxConcurrentCalls, so an inline Azure Service Bus listener
/// ran on the SDK default of 1 -- one message at a time per endpoint -- reachable only through the
/// raw ConfigureProcessor hook.
/// </summary>
public class maximum_concurrent_calls_3494
{
    [Fact]
    public void defaults_to_null_so_the_sdk_default_still_applies()
    {
        var transport = new AzureServiceBusTransport();
        transport.Queues["incoming"].MaximumConcurrentCalls.ShouldBeNull();
    }

    [Fact]
    public void must_be_at_least_one()
    {
        var transport = new AzureServiceBusTransport();
        var queue = transport.Queues["incoming"];

        Should.Throw<ArgumentOutOfRangeException>(() => queue.MaximumConcurrentCalls = 0);
    }

    [Fact]
    public void queue_listener_configuration_sets_it()
    {
        var transport = new AzureServiceBusTransport();
        var queue = transport.Queues["incoming"];

        var configuration = new AzureServiceBusQueueListenerConfiguration(queue);
        configuration.MaximumConcurrentCalls(12);
        ((IDelayedEndpointConfiguration)configuration).Apply();

        queue.MaximumConcurrentCalls.ShouldBe(12);
    }

    [Fact]
    public void subscription_listener_configuration_sets_it()
    {
        var transport = new AzureServiceBusTransport();
        var subscription = transport.Topics["topic1"].FindOrCreateSubscription("sub1");

        var configuration = new AzureServiceBusSubscriptionListenerConfiguration(subscription);
        configuration.MaximumConcurrentCalls(4);
        ((IDelayedEndpointConfiguration)configuration).Apply();

        subscription.MaximumConcurrentCalls.ShouldBe(4);
    }

    [Fact]
    public void flows_onto_the_processor_options()
    {
        var transport = new AzureServiceBusTransport();
        var queue = transport.Queues["incoming"];
        queue.MaximumConcurrentCalls = 8;

        AzureServiceBusTransport.BuildProcessorOptions(queue).MaxConcurrentCalls.ShouldBe(8);
    }

    [Fact]
    public void configure_processor_still_wins_over_the_endpoint_value()
    {
        var transport = new AzureServiceBusTransport();
        var queue = transport.Queues["incoming"];
        queue.MaximumConcurrentCalls = 8;
        queue.ConfigureProcessor = o => o.MaxConcurrentCalls = 3;

        AzureServiceBusTransport.BuildProcessorOptions(queue).MaxConcurrentCalls.ShouldBe(3);
    }

    [Fact]
    public void session_processor_keeps_per_session_fifo_unless_asked_otherwise()
    {
        var transport = new AzureServiceBusTransport();
        var queue = transport.Queues["incoming"];
        queue.ConfigureSessionProcessor = _ => { };

        AzureServiceBusTransport.BuildSessionProcessorOptions(queue).MaxConcurrentCallsPerSession.ShouldBe(1);
    }

    [Fact]
    public void session_processor_honors_the_opt_in()
    {
        var transport = new AzureServiceBusTransport();
        var queue = transport.Queues["incoming"];
        queue.MaximumConcurrentCalls = 5;
        queue.ConfigureSessionProcessor = _ => { };

        AzureServiceBusTransport.BuildSessionProcessorOptions(queue).MaxConcurrentCallsPerSession.ShouldBe(5);
    }
}
