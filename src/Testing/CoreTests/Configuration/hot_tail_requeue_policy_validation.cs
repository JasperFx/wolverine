using JasperFx.Core;
using Shouldly;
using Wolverine;
using Wolverine.Configuration;
using Wolverine.ErrorHandling;
using Wolverine.Runtime;
using Wolverine.Transports;
using Wolverine.Transports.Sending;
using Xunit;

namespace CoreTests.Configuration;

/// <summary>
/// GH-4060. A listener whose cursor is ephemeral -- Pulsar's TailFromLatest() is the one transport feature that
/// does this today -- has nowhere to redeliver a deferred message from, so its DeferAsync is necessarily a no-op.
/// Requeue-shaped error handling configured against it does nothing at all, which nothing in the API says, so it
/// is warned about at bootstrap.
/// </summary>
public class hot_tail_requeue_policy_validation
{
    private static Endpoint listenerThatCannotRedeliver() =>
        new CursorlessEndpoint("stub://hot-tail".ToUri()) { IsListener = true };

    private static Endpoint ordinaryListener() =>
        new RedeliveringEndpoint("stub://ordinary".ToUri()) { IsListener = true };

    [Fact]
    public void warns_when_a_cursorless_listener_carries_requeue_policy()
    {
        var problem = ListenerConfigurationValidator
            .Validate(listenerThatCannotRedeliver(), requeuePoliciesConfigured: true)
            .Single();

        problem.Severity.ShouldBe(ListenerConfigurationSeverity.Warning);
        problem.Message.ShouldContain("stub://hot-tail");
        problem.Message.ShouldContain("Requeue()");
        problem.Message.ShouldContain("TailFromLatest()");

        // The whole point of the warning is telling people what DOES still work, not just what doesn't.
        problem.Message.ShouldContain("RetryTimes()");
        problem.Message.ShouldContain("dead letter table");
    }

    [Fact]
    public void does_not_warn_when_no_requeue_policy_is_configured()
    {
        ListenerConfigurationValidator
            .Validate(listenerThatCannotRedeliver(), requeuePoliciesConfigured: false)
            .ShouldBeEmpty();
    }

    [Fact]
    public void does_not_warn_for_a_listener_that_can_redeliver()
    {
        ListenerConfigurationValidator
            .Validate(ordinaryListener(), requeuePoliciesConfigured: true)
            .ShouldBeEmpty();
    }

    [Fact]
    public void does_not_warn_when_the_endpoint_is_not_listening()
    {
        var endpoint = new CursorlessEndpoint("stub://hot-tail".ToUri());
        endpoint.IsListener.ShouldBeFalse();

        ListenerConfigurationValidator.Validate(endpoint, requeuePoliciesConfigured: true).ShouldBeEmpty();
    }

    /// <summary>
    /// The warning fires in every mode, not only Inline: a cursorless listener ignores requeue policies whether or
    /// not Wolverine's local execution block sits in front of it.
    /// </summary>
    [Theory]
    [InlineData(EndpointMode.Inline)]
    [InlineData(EndpointMode.BufferedInMemory)]
    public void warns_in_every_mode(EndpointMode mode)
    {
        var endpoint = listenerThatCannotRedeliver();
        endpoint.Mode = mode;

        ListenerConfigurationValidator.Validate(endpoint, requeuePoliciesConfigured: true)
            .ShouldContain(x => x.Message.Contains("Requeue()"));
    }

    #region what counts as a requeue policy

    [Fact]
    public void an_empty_collection_has_no_requeue_policies()
    {
        new FailureRuleCollection().AnyRequeuePolicies().ShouldBeFalse();
    }

    [Fact]
    public void in_lane_retries_are_not_requeue_policies()
    {
        var rules = new PolicyHolder();
        rules.OnException<DivideByZeroException>().RetryTimes(3);
        rules.OnException<BadImageFormatException>().RetryWithCooldown(1.Milliseconds());
        rules.OnException<TimeoutException>().ScheduleRetry(1.Seconds());

        rules.Failures.AnyRequeuePolicies().ShouldBeFalse();
    }

    [Fact]
    public void requeue_counts()
    {
        var rules = new PolicyHolder();
        rules.OnException<DivideByZeroException>().Requeue(3);

        rules.Failures.AnyRequeuePolicies().ShouldBeTrue();
    }

    [Fact]
    public void pause_then_requeue_counts()
    {
        var rules = new PolicyHolder();
        rules.OnException<DivideByZeroException>().PauseThenRequeue(50.Milliseconds());

        rules.Failures.AnyRequeuePolicies().ShouldBeTrue();
    }

    [Fact]
    public void requeue_indefinitely_counts()
    {
        var rules = new PolicyHolder();
        rules.OnException<DivideByZeroException>().RequeueIndefinitely();

        rules.Failures.AnyRequeuePolicies().ShouldBeTrue();
    }

    [Fact]
    public void maximum_attempts_counts_because_it_synthesizes_a_requeue_ladder()
    {
        var rules = new FailureRuleCollection { MaximumAttempts = 3 };

        rules.AnyRequeuePolicies().ShouldBeTrue();
    }

    #endregion

    private class PolicyHolder : IWithFailurePolicies
    {
        public FailureRuleCollection Failures { get; } = new();
    }

    // Deliberately minimal stand-ins: this rule is about a property of the endpoint, and building a real Pulsar
    // endpoint here would drag the Pulsar assembly into CoreTests. Wolverine.Pulsar.Tests covers the real one.
    private class CursorlessEndpoint(Uri uri) : Endpoint(uri, EndpointRole.Application)
    {
        protected internal override bool supportsRedelivery => false;

        public override ValueTask<IListener> BuildListenerAsync(IWolverineRuntime runtime, IReceiver receiver)
            => throw new NotSupportedException();

        protected override ISender CreateSender(IWolverineRuntime runtime) => throw new NotSupportedException();
    }

    private class RedeliveringEndpoint(Uri uri) : Endpoint(uri, EndpointRole.Application)
    {
        public override ValueTask<IListener> BuildListenerAsync(IWolverineRuntime runtime, IReceiver receiver)
            => throw new NotSupportedException();

        protected override ISender CreateSender(IWolverineRuntime runtime) => throw new NotSupportedException();
    }
}
