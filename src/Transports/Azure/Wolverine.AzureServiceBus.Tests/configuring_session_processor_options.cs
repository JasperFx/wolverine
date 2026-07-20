using Azure.Messaging.ServiceBus;
using JasperFx.Core;
using Shouldly;
using Wolverine.AzureServiceBus.Internal;
using Wolverine.Configuration;
using Xunit;

namespace Wolverine.AzureServiceBus.Tests;

public class configuring_session_processor_options
{
    [Fact]
    public void configure_session_processor_stores_the_action_and_requires_sessions_on_the_queue()
    {
        var transport = new AzureServiceBusTransport();
        var queue = transport.Queues["incoming"];

        var configuration = new AzureServiceBusQueueListenerConfiguration(queue);
        configuration.ConfigureSessionProcessor(o => o.MaxConcurrentSessions = 4);

        ((IDelayedEndpointConfiguration)configuration).Apply();

        queue.ConfigureSessionProcessor.ShouldNotBeNull();
        queue.Options.RequiresSession.ShouldBeTrue();
    }

    [Fact]
    public void configure_session_processor_stores_the_action_and_requires_sessions_on_the_subscription()
    {
        var transport = new AzureServiceBusTransport();
        var topic = transport.Topics["topic1"];
        var subscription = topic.FindOrCreateSubscription("sub1");

        var configuration = new AzureServiceBusSubscriptionListenerConfiguration(subscription);
        configuration.ConfigureSessionProcessor(o => o.MaxConcurrentSessions = 4);

        ((IDelayedEndpointConfiguration)configuration).Apply();

        subscription.ConfigureSessionProcessor.ShouldNotBeNull();
        subscription.Options.RequiresSession.ShouldBeTrue();
    }

    [Fact]
    public void require_sessions_with_only_these_identifiers_populates_session_ids()
    {
        var transport = new AzureServiceBusTransport();
        var queue = transport.Queues["incoming"];

        var configuration = new AzureServiceBusQueueListenerConfiguration(queue);
        configuration.RequireSessionsWithOnlyTheseIdentifiers("A", "B");

        ((IDelayedEndpointConfiguration)configuration).Apply();

        queue.Options.RequiresSession.ShouldBeTrue();

        var options = AzureServiceBusTransport.BuildSessionProcessorOptions(queue);
        options.SessionIds.ShouldBe(new[] { "A", "B" });
    }

    [Fact]
    public void build_session_processor_options_reasserts_peek_lock_and_disables_autocomplete()
    {
        var transport = new AzureServiceBusTransport();
        var queue = transport.Queues["incoming"];

        // A user trying to break Wolverine's acknowledgement contract must not win
        queue.ConfigureSessionProcessor = o =>
        {
            o.ReceiveMode = ServiceBusReceiveMode.ReceiveAndDelete;
            o.AutoCompleteMessages = true;
        };

        var options = AzureServiceBusTransport.BuildSessionProcessorOptions(queue);

        options.ReceiveMode.ShouldBe(ServiceBusReceiveMode.PeekLock);
        options.AutoCompleteMessages.ShouldBeFalse();
    }

    [Fact]
    public void build_session_processor_options_maps_listener_count_to_max_concurrent_sessions()
    {
        var transport = new AzureServiceBusTransport();
        var queue = transport.Queues["incoming"];

        var configuration = new AzureServiceBusQueueListenerConfiguration(queue);
        configuration.RequireSessions(8).ConfigureSessionProcessor(_ => { });

        ((IDelayedEndpointConfiguration)configuration).Apply();

        var options = AzureServiceBusTransport.BuildSessionProcessorOptions(queue);
        options.MaxConcurrentSessions.ShouldBe(8);

        // FIFO ordering per session is preserved
        options.MaxConcurrentCallsPerSession.ShouldBe(1);
    }

    [Fact]
    public void build_session_processor_options_composes_multiple_actions()
    {
        var transport = new AzureServiceBusTransport();
        var queue = transport.Queues["incoming"];

        var configuration = new AzureServiceBusQueueListenerConfiguration(queue);

        // The SessionIds sugar and an explicit customization must both apply
        configuration
            .RequireSessionsWithOnlyTheseIdentifiers("only-me")
            .ConfigureSessionProcessor(o => o.MaxAutoLockRenewalDuration = 10.Minutes());

        ((IDelayedEndpointConfiguration)configuration).Apply();

        var options = AzureServiceBusTransport.BuildSessionProcessorOptions(queue);

        options.SessionIds.ShouldBe(new[] { "only-me" });
        options.MaxAutoLockRenewalDuration.ShouldBe(10.Minutes());
    }

    [Fact]
    public void session_listener_defaults_to_the_legacy_loop_when_no_customization()
    {
        var transport = new AzureServiceBusTransport();
        var queue = transport.Queues["incoming"];

        var configuration = new AzureServiceBusQueueListenerConfiguration(queue);
        configuration.RequireSessions();

        ((IDelayedEndpointConfiguration)configuration).Apply();

        // Zero behavior change for existing session users: the processor path is opt-in only
        queue.Options.RequiresSession.ShouldBeTrue();
        queue.ConfigureSessionProcessor.ShouldBeNull();
    }
}
