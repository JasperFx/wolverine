using IntegrationTests;
using Microsoft.Extensions.Hosting;
using Shouldly;
using Wolverine.AzureServiceBus.Internal;
using Wolverine.Configuration;
using Xunit;

namespace Wolverine.AzureServiceBus.Tests;

/// <summary>
/// GH-4049. Azure Service Bus sessions and <see cref="EndpointMode.NativeAck" /> are mutually exclusive, and the
/// refusal has to survive either fluent ordering. <c>RequireSessions()</c> and <c>ProcessInParallelWithNativeAcks()</c>
/// are both delayed configuration applied inside <see cref="Endpoint.Compile" />, so a guard in the <c>Mode</c>
/// setter -- or in <c>supportsNativeAck</c>, which the setter consults -- would catch one ordering and miss the
/// other. That is the same order dependence GH-3712 fixed for <c>ProcessInline()</c>, and the fix is the same:
/// validate the final compiled state.
/// </summary>
public class native_ack_with_sessions_4049
{
    /// <summary>
    /// These subclasses predate GH-4051, when Azure Service Bus had not yet opted into native acks and the mode gate
    /// would otherwise have thrown before any of these tests reached the session rejection. They are kept because
    /// <see cref="NativeAckCapableQueue.SessionsRequiredWhenTheModeWasChecked" /> is still the only way to observe
    /// what a guard living in the Mode setter would have been able to see -- see
    /// <see cref="a_guard_in_the_mode_setter_would_be_ordering_dependent" />.
    /// </summary>
    private class NativeAckCapableQueue : AzureServiceBusQueue
    {
        public NativeAckCapableQueue(AzureServiceBusTransport parent, string queueName) : base(parent, queueName)
        {
        }

        /// <summary>
        /// Records what a guard living in the Mode setter would have been able to see, because this is the only
        /// member that setter consults on the way to accepting EndpointMode.NativeAck.
        /// </summary>
        public bool? SessionsRequiredWhenTheModeWasChecked { get; private set; }

        protected override bool supportsNativeAck
        {
            get
            {
                SessionsRequiredWhenTheModeWasChecked = Options.RequiresSession;
                return true;
            }
        }
    }

    private class NativeAckCapableSubscription : AzureServiceBusSubscription
    {
        public NativeAckCapableSubscription(AzureServiceBusTransport parent, AzureServiceBusTopic topic,
            string subscriptionName) : base(parent, topic, subscriptionName)
        {
        }

        protected override bool supportsNativeAck => true;
    }

    private static NativeAckCapableQueue compiledQueue(Action<AzureServiceBusQueueListenerConfiguration> configure,
        string queueName = "sessions-and-native-acks")
    {
        var transport = new AzureServiceBusTransport();
        var queue = new NativeAckCapableQueue(transport, queueName);
        transport.Queues[queueName] = queue;

        var configuration = new AzureServiceBusQueueListenerConfiguration(queue);
        configure(configuration);

        // Exactly what Endpoint.Compile() does with the delayed configuration, in registration order
        ((IDelayedEndpointConfiguration)configuration).Apply();

        return queue;
    }

    private static NativeAckCapableSubscription compiledSubscription(
        Action<AzureServiceBusSubscriptionListenerConfiguration> configure,
        string subscriptionName = "sessions-and-native-acks")
    {
        var transport = new AzureServiceBusTransport();
        var topic = transport.Topics["topic-4049"];
        var subscription = new NativeAckCapableSubscription(transport, topic, subscriptionName);
        transport.Subscriptions.Add(subscription);

        var configuration = new AzureServiceBusSubscriptionListenerConfiguration(subscription);
        configure(configuration);

        ((IDelayedEndpointConfiguration)configuration).Apply();

        return subscription;
    }

    private static void shouldBeTheSessionRejection(Endpoint endpoint)
    {
        var problem = ListenerConfigurationValidator.Validate(endpoint).ShouldHaveSingleItem();

        problem.Severity.ShouldBe(ListenerConfigurationSeverity.Fatal);
        problem.Message.ShouldContain("RequireSessions()");
        problem.Message.ShouldContain("ProcessInParallelWithNativeAcks()");
        problem.Message.ShouldContain(endpoint.Uri.ToString());
    }

    [Fact]
    public void queue_sessions_then_native_acks_is_fatal()
    {
        var queue = compiledQueue(x => x.RequireSessions().ProcessInParallelWithNativeAcks());

        queue.Mode.ShouldBe(EndpointMode.NativeAck);
        queue.Options.RequiresSession.ShouldBeTrue();

        shouldBeTheSessionRejection(queue);
    }

    [Fact]
    public void queue_native_acks_then_sessions_is_fatal()
    {
        // The reverse ordering is the whole point: the Mode setter saw a session-less queue here, so
        // nothing it could have asked would have caught this pair.
        var queue = compiledQueue(x => x.ProcessInParallelWithNativeAcks().RequireSessions());

        queue.Mode.ShouldBe(EndpointMode.NativeAck);
        queue.Options.RequiresSession.ShouldBeTrue();

        shouldBeTheSessionRejection(queue);
    }

    [Fact]
    public void subscription_sessions_then_native_acks_is_fatal()
    {
        var subscription = compiledSubscription(x => x.RequireSessions().ProcessInParallelWithNativeAcks());

        shouldBeTheSessionRejection(subscription);
    }

    [Fact]
    public void subscription_native_acks_then_sessions_is_fatal()
    {
        var subscription = compiledSubscription(x => x.ProcessInParallelWithNativeAcks().RequireSessions());

        shouldBeTheSessionRejection(subscription);
    }

    /// <summary>
    /// The premise the whole design rests on: a guard in the Mode setter -- or in <c>supportsNativeAck</c>, the only
    /// member that setter consults -- sees a DIFFERENT endpoint depending on which fluent call was written first.
    /// Written sessions-first it sees a session queue; written native-acks-first it sees a session-less one and
    /// would wave the pairing through. Only a check over the compiled state is ordering-proof.
    /// </summary>
    [Fact]
    public void a_guard_in_the_mode_setter_would_be_ordering_dependent()
    {
        compiledQueue(x => x.RequireSessions().ProcessInParallelWithNativeAcks())
            .SessionsRequiredWhenTheModeWasChecked.ShouldBe(true);

        compiledQueue(x => x.ProcessInParallelWithNativeAcks().RequireSessions())
            .SessionsRequiredWhenTheModeWasChecked.ShouldBe(false);
    }

    [Fact]
    public void native_acks_without_sessions_is_perfectly_fine()
    {
        var queue = compiledQueue(x => x.ProcessInParallelWithNativeAcks(), "native-acks-only");

        queue.Mode.ShouldBe(EndpointMode.NativeAck);
        ListenerConfigurationValidator.Validate(queue).ShouldBeEmpty();
    }

    [Fact]
    public void sessions_without_native_acks_is_perfectly_fine()
    {
        var queue = compiledQueue(x => x.RequireSessions().ProcessInline(), "sessions-only");

        queue.Options.RequiresSession.ShouldBeTrue();
        ListenerConfigurationValidator.Validate(queue).ShouldBeEmpty();
    }

    /// <summary>
    /// The listener selection in AzureServiceBusTransport.Listening asks about sessions BEFORE it asks about the
    /// mode, so this pair would otherwise take the session branch in silence. Second gate, for any path that
    /// builds a listener without going through bootstrap validation.
    /// </summary>
    [Fact]
    public void the_listener_selection_guard_refuses_the_pair()
    {
        var queue = compiledQueue(x => x.RequireSessions().ProcessInParallelWithNativeAcks());

        var ex = Should.Throw<InvalidListenerConfigurationException>(() => queue.AssertSessionsAreCompatibleWithMode());
        ex.Message.ShouldContain("RequireSessions()");
    }

    [Fact]
    public void the_listener_selection_guard_allows_sessions_without_native_acks()
    {
        var queue = compiledQueue(x => x.RequireSessions().ProcessInline(), "sessions-only");

        Should.NotThrow(() => queue.AssertSessionsAreCompatibleWithMode());
    }

    [Fact]
    public void the_listener_selection_guard_allows_native_acks_without_sessions()
    {
        var queue = compiledQueue(x => x.ProcessInParallelWithNativeAcks(), "native-acks-only");

        Should.NotThrow(() => queue.AssertSessionsAreCompatibleWithMode());
    }

    /// <summary>
    /// End to end: this is what the user actually experiences. The refusal is Fatal, so the host does not start.
    /// External transports are stubbed so no broker is needed -- the validator deliberately runs anyway, so a test
    /// host surfaces the same misconfiguration a deployed one would.
    /// </summary>
    [Fact]
    public async Task the_host_refuses_to_start()
    {
        var ex = await Should.ThrowAsync<InvalidListenerConfigurationException>(async () =>
        {
            using var host = await Host.CreateDefaultBuilder().UseWolverine(opts =>
            {
                opts.StubAllExternalTransports();
                opts.UseAzureServiceBus(Servers.AzureServiceBusConnectionString);

                var transport = opts.AzureServiceBusTransport();
                transport.Queues["refused-at-bootstrap"] =
                    new NativeAckCapableQueue(transport, "refused-at-bootstrap");

                opts.ListenToAzureServiceBusQueue("refused-at-bootstrap")
                    .ProcessInParallelWithNativeAcks()
                    .RequireSessions();
            }).StartAsync(TestContext.Current.CancellationToken);
        });

        ex.Message.ShouldContain("RequireSessions()");
        ex.Message.ShouldContain("refused-at-bootstrap");
    }

    /// <summary>
    /// GH-4051 replaced this test's original assertion. It used to pin that a shipping queue refused native acks in
    /// the Mode setter, which is what made the subclasses above necessary. Azure Service Bus has since adopted the
    /// mode, so a real queue now accepts it -- and the session rejection above still has to be the ONLY thing that
    /// refuses the pairing, which is exactly what this now checks.
    /// </summary>
    [Fact]
    public void the_shipping_queue_now_accepts_native_acks()
    {
        var transport = new AzureServiceBusTransport();
        var queue = transport.Queues["adopted-in-4051"];

        Should.NotThrow(() => queue.Mode = EndpointMode.NativeAck);
        queue.Mode.ShouldBe(EndpointMode.NativeAck);

        // ...and a real subscription too
        var topic = transport.Topics["adopted-topic-4051"];
        var subscription = new AzureServiceBusSubscription(transport, topic, "adopted-subscription-4051");
        Should.NotThrow(() => subscription.Mode = EndpointMode.NativeAck);

        // ...but never a topic, which is only ever published to
        Should.Throw<InvalidOperationException>(() => topic.Mode = EndpointMode.NativeAck)
            .Message.ShouldContain("does not support EndpointMode.NativeAck");
    }
}
