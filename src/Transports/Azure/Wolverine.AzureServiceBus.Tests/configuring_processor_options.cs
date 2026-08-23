using Azure.Messaging.ServiceBus;
using JasperFx.Core;
using Shouldly;
using Wolverine.AzureServiceBus.Internal;
using Wolverine.Configuration;
using Xunit;

namespace Wolverine.AzureServiceBus.Tests;

public class configuring_processor_options
{
    [Fact]
    public void configure_processor_action_is_stored_on_the_queue_endpoint()
    {
        var transport = new AzureServiceBusTransport();
        var queue = transport.Queues["incoming"];

        var configuration = new AzureServiceBusQueueListenerConfiguration(queue);
        configuration.ConfigureProcessor(o => o.MaxAutoLockRenewalDuration = 30.Minutes());

        // Apply the delayed configuration as Wolverine would at bootstrapping time
        ((IDelayedEndpointConfiguration)configuration).Apply();

        queue.ConfigureProcessor.ShouldNotBeNull();
    }

    [Fact]
    public void configure_processor_action_is_stored_on_the_subscription_endpoint()
    {
        var transport = new AzureServiceBusTransport();
        var topic = transport.Topics["topic1"];
        var subscription = topic.FindOrCreateSubscription("sub1");

        var configuration = new AzureServiceBusSubscriptionListenerConfiguration(subscription);
        configuration.ConfigureProcessor(o => o.MaxAutoLockRenewalDuration = 30.Minutes());

        ((IDelayedEndpointConfiguration)configuration).Apply();

        subscription.ConfigureProcessor.ShouldNotBeNull();
    }

    [Fact]
    public void build_processor_options_applies_the_configured_action()
    {
        var transport = new AzureServiceBusTransport();
        var queue = transport.Queues["incoming"];
        queue.ConfigureProcessor = o =>
        {
            o.MaxAutoLockRenewalDuration = 30.Minutes();
            o.PrefetchCount = 12;
        };

        var options = AzureServiceBusTransport.BuildProcessorOptions(queue);

        options.MaxAutoLockRenewalDuration.ShouldBe(30.Minutes());
        options.PrefetchCount.ShouldBe(12);
    }

    [Fact]
    public void build_processor_options_reasserts_peek_lock_receive_mode()
    {
        var transport = new AzureServiceBusTransport();
        var queue = transport.Queues["incoming"];

        // A user trying to switch to ReceiveAndDelete would break Wolverine's
        // inline acknowledgement, so the transport must re-assert PeekLock.
        queue.ConfigureProcessor = o => o.ReceiveMode = ServiceBusReceiveMode.ReceiveAndDelete;

        var options = AzureServiceBusTransport.BuildProcessorOptions(queue);

        options.ReceiveMode.ShouldBe(ServiceBusReceiveMode.PeekLock);
    }

    [Fact]
    public void build_processor_options_turns_off_auto_complete_by_default()
    {
        var transport = new AzureServiceBusTransport();
        var queue = transport.Queues["incoming"];

        // GH-4018: the SDK default is true, which lets the processor complete a message that
        // Wolverine failed to settle on the first attempt -- a dead letter whose first try threw
        // is then completed behind the retry's back and is neither dead lettered nor redelivered.
        AzureServiceBusTransport.BuildProcessorOptions(queue).AutoCompleteMessages.ShouldBeFalse();
    }

    [Fact]
    public void build_processor_options_reasserts_auto_complete_off_over_user_configuration()
    {
        var transport = new AzureServiceBusTransport();
        var queue = transport.Queues["incoming"];
        queue.ConfigureProcessor = o => o.AutoCompleteMessages = true;

        var options = AzureServiceBusTransport.BuildProcessorOptions(queue);

        // Same contract as the session processor path and as ReceiveMode: Wolverine owns settlement
        options.AutoCompleteMessages.ShouldBeFalse();
    }

    [Fact]
    public void build_processor_options_is_valid_when_no_action_is_configured()
    {
        var transport = new AzureServiceBusTransport();
        var queue = transport.Queues["incoming"];

        var options = AzureServiceBusTransport.BuildProcessorOptions(queue);

        options.ShouldNotBeNull();
        options.ReceiveMode.ShouldBe(ServiceBusReceiveMode.PeekLock);
        options.AutoCompleteMessages.ShouldBeFalse();
    }
}
