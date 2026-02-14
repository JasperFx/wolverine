using JasperFx.Core;
using Shouldly;
using Wolverine.ComplianceTests;
using Wolverine.ErrorHandling;
using Xunit;

namespace CoreTests.ErrorHandling;

public class SendingFailurePoliciesTests
{
    private readonly SendingFailurePolicies _policies = new();

    [Fact]
    public void has_no_rules_by_default()
    {
        _policies.HasAnyRules.ShouldBeFalse();
    }

    [Fact]
    public void has_rules_after_configuration()
    {
        _policies.OnException<DivideByZeroException>().Discard();
        _policies.HasAnyRules.ShouldBeTrue();
    }

    [Fact]
    public void returns_null_when_no_rules_match()
    {
        _policies.OnException<DivideByZeroException>().Discard();

        var envelope = ObjectMother.Envelope();
        envelope.SendAttempts = 1;

        var continuation = _policies.DetermineAction(new InvalidOperationException(), envelope);
        continuation.ShouldBeNull();
    }

    [Fact]
    public void returns_continuation_when_rule_matches()
    {
        _policies.OnException<DivideByZeroException>().Discard();

        var envelope = ObjectMother.Envelope();
        envelope.SendAttempts = 1;

        var continuation = _policies.DetermineAction(new DivideByZeroException(), envelope);
        continuation.ShouldNotBeNull();
        continuation.ShouldBeOfType<DiscardEnvelope>();
    }

    [Fact]
    public void uses_send_attempts_not_handler_attempts()
    {
        // Configure: retry once, then discard
        _policies.OnException<DivideByZeroException>()
            .RetryOnce().Then.Discard();

        var envelope = ObjectMother.Envelope();

        // First send attempt → should get retry
        envelope.SendAttempts = 1;
        envelope.Attempts = 42; // handler attempts should be irrelevant
        var continuation = _policies.DetermineAction(new DivideByZeroException(), envelope);
        continuation.ShouldBeOfType<RetryInlineContinuation>();

        // Handler attempts should be restored after evaluation
        envelope.Attempts.ShouldBe(42);

        // Second send attempt → should get discard
        envelope.SendAttempts = 2;
        continuation = _policies.DetermineAction(new DivideByZeroException(), envelope);
        continuation.ShouldBeOfType<DiscardEnvelope>();

        // Handler attempts should still be restored
        envelope.Attempts.ShouldBe(42);
    }

    [Fact]
    public void combine_with_parent_policies()
    {
        // Local policy handles DivideByZeroException
        _policies.OnException<DivideByZeroException>().Discard();

        // Parent policy handles InvalidOperationException
        var parent = new SendingFailurePolicies();
        parent.OnException<InvalidOperationException>().MoveToErrorQueue();

        var combined = _policies.CombineWith(parent);

        var envelope = ObjectMother.Envelope();
        envelope.SendAttempts = 1;

        // Local rule should match
        combined.DetermineAction(new DivideByZeroException(), envelope)
            .ShouldBeOfType<DiscardEnvelope>();

        // Parent rule should match
        combined.DetermineAction(new InvalidOperationException(), envelope)
            .ShouldNotBeNull();

        // Neither should match
        combined.DetermineAction(new BadImageFormatException(), envelope)
            .ShouldBeNull();
    }

    [Fact]
    public void local_policies_take_priority_over_parent()
    {
        // Local: discard on Exception
        _policies.OnException<Exception>().Discard();

        // Parent: move to error queue on Exception
        var parent = new SendingFailurePolicies();
        parent.OnException<Exception>().MoveToErrorQueue();

        var combined = _policies.CombineWith(parent);

        var envelope = ObjectMother.Envelope();
        envelope.SendAttempts = 1;

        // Local discard should win over parent's MoveToErrorQueue
        combined.DetermineAction(new InvalidOperationException(), envelope)
            .ShouldBeOfType<DiscardEnvelope>();
    }

    [Fact]
    public void pause_sending_action_via_fluent_api()
    {
        _policies.OnException<DivideByZeroException>()
            .PauseSending(30.Seconds());

        var envelope = ObjectMother.Envelope();
        envelope.SendAttempts = 1;

        var continuation = _policies.DetermineAction(new DivideByZeroException(), envelope);
        continuation.ShouldNotBeNull();
        continuation.ShouldBeOfType<PauseSendingContinuation>()
            .PauseTime.ShouldBe(30.Seconds());
    }

    [Fact]
    public void and_pause_sending_as_additional_action()
    {
        _policies.OnException<DivideByZeroException>()
            .MoveToErrorQueue().AndPauseSending(1.Minutes());

        var envelope = ObjectMother.Envelope();
        envelope.SendAttempts = 1;

        var continuation = _policies.DetermineAction(new DivideByZeroException(), envelope);
        continuation.ShouldNotBeNull();

        // Should be a composite continuation containing both actions
        var composite = continuation.ShouldBeOfType<CompositeContinuation>();
        composite.Inner.OfType<PauseSendingContinuation>().ShouldHaveSingleItem()
            .PauseTime.ShouldBe(1.Minutes());
    }

    [Fact]
    public void schedule_retry_then_pause_sending()
    {
        _policies.OnException<IOException>()
            .ScheduleRetry(5.Seconds(), 30.Seconds()).Then
            .PauseSending(2.Minutes());

        var envelope = ObjectMother.Envelope();

        // First attempt → schedule retry at 5s
        envelope.SendAttempts = 1;
        var continuation = _policies.DetermineAction(new IOException(), envelope);
        continuation.ShouldBeOfType<ScheduledRetryContinuation>();

        // Second attempt → schedule retry at 30s
        envelope.SendAttempts = 2;
        continuation = _policies.DetermineAction(new IOException(), envelope);
        continuation.ShouldBeOfType<ScheduledRetryContinuation>();

        // Third attempt → pause sending
        envelope.SendAttempts = 3;
        continuation = _policies.DetermineAction(new IOException(), envelope);
        continuation.ShouldBeOfType<PauseSendingContinuation>()
            .PauseTime.ShouldBe(2.Minutes());
    }
}
