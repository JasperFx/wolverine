using Shouldly;
using Wolverine.ComplianceTests;
using Wolverine.ErrorHandling;
using Wolverine.ErrorHandling.Matches;
using Wolverine.Runtime;
using Wolverine.Runtime.Handlers;
using Xunit;

namespace CoreTests.ErrorHandling;

/// <summary>
/// An <see cref="IContinuationSource" /> declines an envelope by returning null, and a
/// <see cref="FailureRule" /> whose sources all decline has to be skipped so the next rule gets a turn.
/// Wolverine.Pulsar depends on this: UsePulsar() registers a global AlwaysMatches rule ahead of every
/// user policy, and that rule may only claim failures Pulsar's native retry/DLQ can actually act on.
/// </summary>
public class declining_continuation_sources
{
    private readonly Envelope theEnvelope = ObjectMother.Envelope();

    [Fact]
    public void rule_is_not_handled_when_its_only_slot_declines()
    {
        var rule = new FailureRule(new TypeMatch<DivideByZeroException>());
        rule.AddSlot(new DecliningSource());

        rule.TryCreateContinuation(new DivideByZeroException(), theEnvelope, out var continuation)
            .ShouldBeFalse();

        continuation.ShouldBeOfType<NullContinuation>();
    }

    [Fact]
    public void rule_is_not_handled_when_both_the_slot_and_the_infinite_source_decline()
    {
        var rule = new FailureRule(new TypeMatch<DivideByZeroException>());
        rule.AddSlot(new DecliningSource());
        rule.InfiniteSource = new DecliningSource();

        rule.TryCreateContinuation(new DivideByZeroException(), theEnvelope, out _).ShouldBeFalse();

        // ...and on an attempt past the last slot, where only the infinite source is consulted
        theEnvelope.Attempts = 7;
        rule.TryCreateContinuation(new DivideByZeroException(), theEnvelope, out _).ShouldBeFalse();
    }

    [Fact]
    public void a_declining_slot_still_falls_through_to_the_infinite_source()
    {
        var rule = new FailureRule(new TypeMatch<DivideByZeroException>());
        rule.AddSlot(new DecliningSource());
        rule.InfiniteSource = RequeueContinuation.Instance;

        rule.TryCreateContinuation(new DivideByZeroException(), theEnvelope, out var continuation)
            .ShouldBeTrue();

        continuation.ShouldBe(RequeueContinuation.Instance);
    }

    [Fact]
    public void running_out_of_slots_is_still_a_dead_letter_and_not_a_decline()
    {
        var rule = new FailureRule(new TypeMatch<DivideByZeroException>());
        rule.AddSlot(new DecliningSource());

        // Past the one and only slot, so nothing was even asked -- that is exhaustion, not a decline
        theEnvelope.Attempts = 2;

        rule.TryCreateContinuation(new DivideByZeroException(), theEnvelope, out var continuation)
            .ShouldBeTrue();

        continuation.ShouldBeOfType<MoveToErrorQueue>();
    }

    [Fact]
    public void slot_declines_when_every_one_of_its_sources_declines()
    {
        var slot = new FailureSlot(1, new DecliningSource());
        slot.AddAdditionalSource(new DecliningSource());

        slot.Build(new DivideByZeroException(), theEnvelope).ShouldBeNull();
    }

    [Fact]
    public void slot_uses_the_surviving_source_when_only_some_decline()
    {
        var slot = new FailureSlot(1, new DecliningSource());
        slot.AddAdditionalSource(RequeueContinuation.Instance);

        slot.Build(new DivideByZeroException(), theEnvelope).ShouldBe(RequeueContinuation.Instance);
    }

    [Fact]
    public void a_declining_rule_falls_through_to_the_next_rule_in_the_collection()
    {
        var handlers = new HandlerGraph();

        // Stand-in for the rule UsePulsar() registers: matches everything, declines everything
        var alwaysDeclines = new FailureRule(new TypeMatch<Exception>());
        alwaysDeclines.AddSlot(new DecliningSource());
        alwaysDeclines.InfiniteSource = new DecliningSource();
        handlers.Failures.Add(alwaysDeclines);

        handlers.OnException<DivideByZeroException>().RetryTimes(3);

        handlers.Failures.DetermineExecutionContinuation(new DivideByZeroException(), theEnvelope)
            .ShouldBeOfType<RetryInlineContinuation>();
    }

    [Fact]
    public void a_declining_rule_with_nothing_behind_it_still_ends_in_the_error_queue()
    {
        var handlers = new HandlerGraph();

        var alwaysDeclines = new FailureRule(new TypeMatch<Exception>());
        alwaysDeclines.AddSlot(new DecliningSource());
        handlers.Failures.Add(alwaysDeclines);

        handlers.Failures.DetermineExecutionContinuation(new DivideByZeroException(), theEnvelope)
            .ShouldBeOfType<MoveToErrorQueue>();
    }

    [Fact]
    public void a_declining_rule_does_not_hide_a_later_inline_continuation()
    {
        var handlers = new HandlerGraph();

        var alwaysDeclines = new FailureRule(new TypeMatch<Exception>());
        alwaysDeclines.AddSlot(new DecliningSource());
        handlers.Failures.Add(alwaysDeclines);

        handlers.OnException<DivideByZeroException>().RetryTimes(3);

        handlers.Failures.TryFindInlineContinuation(new DivideByZeroException(), theEnvelope)
            .ShouldBeOfType<RetryInlineContinuation>();
    }

    private class DecliningSource : IContinuationSource
    {
        public string Description => "Declines everything";

        public IContinuation? Build(Exception ex, Envelope envelope) => null;
    }
}
