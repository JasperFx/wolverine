using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Shouldly;
using Wolverine.Configuration;
using Wolverine.Runtime;
using Wolverine.Transports.Stub;
using Xunit;

namespace CoreTests.Configuration;

/// <summary>
/// GH-4059. On any transport where publishing and listening resolve to the same <see cref="Endpoint"/> object
/// -- a RabbitMQ queue, a Redis stream, a Pulsar topic -- a sending-side mode call silently overwrote the
/// listening side's <see cref="EndpointMode"/>, and vice versa. Two people hit it independently within an
/// hour while building the Redis Streams and Pulsar transports.
/// </summary>
/// <remarks>
/// <para>Both sides are delayed configuration, so which one survives depends on the order Wolverine applies
/// the blocks. That is why the collision is reported from the validator rather than guarded in the
/// <see cref="EndpointMode"/> setter: a setter guard catches one ordering and misses the reverse. Same
/// reasoning as GH-3712's parallelism clamp.</para>
///
/// <para>These assert a WARNING rather than a refusal. "Send inline, receive durably" is a coherent thing to
/// want and a single Mode property cannot express it, so refusing would reject a configuration that is
/// meaningful rather than mistaken.</para>
/// </remarks>
public class conflicting_endpoint_mode_4059
{
    private static Endpoint compiledEndpoint(Action<WolverineOptions> configure, string uri)
    {
        using var host = Host.CreateDefaultBuilder().UseWolverine(configure).Build();

        var options = host.Services.GetRequiredService<WolverineOptions>();
        var runtime = host.Services.GetRequiredService<IWolverineRuntime>();

        var endpoint = options.Transports.AllEndpoints().Single(x => x.Uri == new Uri(uri));
        endpoint.Compile(runtime);

        return endpoint;
    }

    [Fact]
    public void send_inline_over_a_durable_listener_is_reported()
    {
        var endpoint = compiledEndpoint(opts =>
        {
            opts.ListenForMessagesFrom("stub://shared-one").UseDurableInbox();
            opts.PublishAllMessages().To("stub://shared-one").SendInline();
        }, "stub://shared-one");

        var problem = ListenerConfigurationValidator.Validate(endpoint)
            .Single(x => x.Message.Contains("Conflicting endpoint mode"));

        problem.Severity.ShouldBe(ListenerConfigurationSeverity.Warning);

        // Both requests are named, so the reader can find the two calls
        problem.Message.ShouldContain("listening side asked for Durable");
        problem.Message.ShouldContain("publishing side asked for Inline");
        problem.Message.ShouldContain("stub://shared-one");

        // ...along with which one actually won, and that the answer is order-dependent
        problem.Message.ShouldContain($"running it as {endpoint.Mode}");
        problem.Message.ShouldContain("order");
    }

    [Fact]
    public void the_conflict_is_reported_whichever_order_the_two_blocks_appear_in()
    {
        // The whole point: a guard in the Mode setter would see only one of these two arrangements
        var publishFirst = compiledEndpoint(opts =>
        {
            opts.PublishAllMessages().To("stub://order-a").SendInline();
            opts.ListenForMessagesFrom("stub://order-a").UseDurableInbox();
        }, "stub://order-a");

        var listenFirst = compiledEndpoint(opts =>
        {
            opts.ListenForMessagesFrom("stub://order-b").UseDurableInbox();
            opts.PublishAllMessages().To("stub://order-b").SendInline();
        }, "stub://order-b");

        foreach (var endpoint in new[] { publishFirst, listenFirst })
        {
            ListenerConfigurationValidator.Validate(endpoint)
                .ShouldContain(x => x.Message.Contains("Conflicting endpoint mode"),
                    $"no conflict reported for {endpoint.Uri}");
        }
    }

    [Fact]
    public void use_durable_outbox_over_an_inline_listener_is_reported()
    {
        // SendInline() is the loudest instance, not the only one
        var endpoint = compiledEndpoint(opts =>
        {
            opts.ListenForMessagesFrom("stub://shared-two").ProcessInline();
            opts.PublishAllMessages().To("stub://shared-two").UseDurableOutbox();
        }, "stub://shared-two");

        var problem = ListenerConfigurationValidator.Validate(endpoint)
            .Single(x => x.Message.Contains("Conflicting endpoint mode"));

        problem.Message.ShouldContain("listening side asked for Inline");
        problem.Message.ShouldContain("publishing side asked for Durable");
    }

    [Fact]
    public void agreeing_sides_are_not_reported()
    {
        var endpoint = compiledEndpoint(opts =>
        {
            opts.ListenForMessagesFrom("stub://agree").UseDurableInbox();
            opts.PublishAllMessages().To("stub://agree").UseDurableOutbox();
        }, "stub://agree");

        ListenerConfigurationValidator.Validate(endpoint)
            .ShouldNotContain(x => x.Message.Contains("Conflicting endpoint mode"));
    }

    [Fact]
    public void a_side_that_never_named_a_mode_is_not_reported()
    {
        // The common case, and the reason this is not simply "Mode was assigned twice": a bare
        // PublishAllMessages().To(...) names no mode at all, so there is nothing to conflict with
        var endpoint = compiledEndpoint(opts =>
        {
            opts.ListenForMessagesFrom("stub://quiet").UseDurableInbox();
            opts.PublishAllMessages().To("stub://quiet");
        }, "stub://quiet");

        endpoint.SubscriberRequestedMode.ShouldBeNull();

        ListenerConfigurationValidator.Validate(endpoint)
            .ShouldNotContain(x => x.Message.Contains("Conflicting endpoint mode"));
    }

    [Fact]
    public void a_listen_only_endpoint_is_not_reported()
    {
        var endpoint = compiledEndpoint(opts =>
        {
            opts.ListenForMessagesFrom("stub://listen-only").ProcessInline();
        }, "stub://listen-only");

        endpoint.ListenerRequestedMode.ShouldBe(EndpointMode.Inline);
        endpoint.SubscriberRequestedMode.ShouldBeNull();

        ListenerConfigurationValidator.Validate(endpoint)
            .ShouldNotContain(x => x.Message.Contains("Conflicting endpoint mode"));
    }

    /// <summary>
    /// GH-4059's sharpest case. The listener asked for a mode that supports partitioned processing, a publish
    /// rule reset it to Inline, and GH-3712's validator then refused the host with a message telling the reader
    /// to use the very call their listener already had.
    /// </summary>
    /// <remarks>
    /// The report's own example used <c>ProcessInParallelWithNativeAcks()</c>, which the stub transport does not
    /// support -- <c>supportsNativeAck</c> is opt-in per transport and setting the mode on a stub throws. The
    /// mode that loses does not matter to this check, only that it was not Inline, so the durable inbox stands in
    /// here and <see cref="the_native_ack_case_from_the_report"/> covers the literal combination.
    /// </remarks>
    [Fact]
    public void partitioning_refused_because_of_a_publish_rule_says_so()
    {
        var endpoint = compiledEndpoint(opts =>
        {
            opts.ListenForMessagesFrom("stub://clobbered")
                .UseDurableInbox()
                .PartitionProcessingByGroupId(PartitionSlots.Five);

            opts.PublishAllMessages().To("stub://clobbered").SendInline();
        }, "stub://clobbered");

        endpoint.Mode.ShouldBe(EndpointMode.Inline);

        var problem = ListenerConfigurationValidator.Validate(endpoint)
            .Single(x => x.Severity == ListenerConfigurationSeverity.Fatal);

        problem.Message.ShouldContain("PartitionProcessingByGroupId()");

        // Blames the call that actually imposed Inline...
        problem.Message.ShouldContain("SendInline()");
        problem.Message.ShouldContain("PUBLISHING side");
        problem.Message.ShouldContain("Nothing on the listening side asked for Inline");

        // ...and does NOT tell the reader to switch to the mode their listener already asked for
        problem.Message.ShouldNotContain("instead of ProcessInline()");
    }

    /// <summary>
    /// The combination the report actually hit: <c>ProcessInParallelWithNativeAcks()</c> plus
    /// <c>PartitionProcessingByGroupId()</c>, reset to Inline by a <c>SendInline()</c> on the same endpoint.
    /// Built directly because <c>supportsNativeAck</c> is opt-in per transport.
    /// </summary>
    [Fact]
    public void the_native_ack_case_from_the_report()
    {
        var endpoint = new NativeAckCapableEndpoint("clobbered-native", new StubTransport())
        {
            IsListener = true,
            GroupShardingSlotNumber = PartitionSlots.Five
        };

        endpoint.RequestListenerMode(EndpointMode.NativeAck);
        endpoint.RequestSubscriberMode(EndpointMode.Inline);

        var problems = ListenerConfigurationValidator.Validate(endpoint).ToArray();

        // The collision itself, reported as a warning...
        problems.ShouldContain(x => x.Severity == ListenerConfigurationSeverity.Warning
                                    && x.Message.Contains("listening side asked for NativeAck"));

        // ...and the lost guarantee, refused, blaming the call that actually caused it
        var fatal = problems.Single(x => x.Severity == ListenerConfigurationSeverity.Fatal);
        fatal.Message.ShouldContain("SendInline()");
        fatal.Message.ShouldNotContain("instead of ProcessInline()");
    }

    /// <summary>An endpoint type that has opted into native acks, as RabbitMQ and Pulsar do.</summary>
    private class NativeAckCapableEndpoint : StubEndpoint
    {
        public NativeAckCapableEndpoint(string queueName, StubTransport transport) : base(queueName, transport)
        {
        }

        protected override bool supportsNativeAck => true;
    }

    [Fact]
    public void partitioning_refused_because_the_listener_asked_for_inline_keeps_the_original_advice()
    {
        var endpoint = compiledEndpoint(opts =>
        {
            opts.ListenForMessagesFrom("stub://own-fault")
                .ProcessInline()
                .PartitionProcessingByGroupId(PartitionSlots.Five);
        }, "stub://own-fault");

        var problem = ListenerConfigurationValidator.Validate(endpoint)
            .Single(x => x.Severity == ListenerConfigurationSeverity.Fatal);

        problem.Message.ShouldContain("instead of ProcessInline()");
        problem.Message.ShouldNotContain("PUBLISHING side");
    }
}
