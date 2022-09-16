using System;
using CoreTests.Messaging;
using Shouldly;
using Wolverine;
using Wolverine.ErrorHandling;
using Wolverine.ErrorHandling.Matches;
using Wolverine.Runtime;
using Xunit;

namespace CoreTests.ErrorHandling;

public class FailureRuleTests
{
    private readonly Envelope theEnvelope = ObjectMother.Envelope();
    private readonly FailureRule theRule;

    public FailureRuleTests()
    {
        theRule = new FailureRule(new TypeMatch<DivideByZeroException>());
    }

    [Fact]
    public void does_not_match_exception()
    {
        ShouldBeBooleanExtensions.ShouldBeFalse(theRule.TryCreateContinuation(new BadImageFormatException(),
            theEnvelope, out var continuation));
    }

    [Fact]
    public void matches_zero_attempt()
    {
        theRule.AddSlot(RequeueContinuation.Instance);

        // Should treat it as the 1st attempt
        theEnvelope.Attempts = 0;

        ShouldBeBooleanExtensions.ShouldBeTrue(theRule.TryCreateContinuation(new DivideByZeroException(), theEnvelope,
            out var continuation));

        ShouldBeTestExtensions.ShouldBe<IContinuation>(continuation, RequeueContinuation.Instance);
    }

    [Fact]
    public void matches_first_attempt()
    {
        theRule.AddSlot(RequeueContinuation.Instance);

        theEnvelope.Attempts = 1;

        ShouldBeBooleanExtensions.ShouldBeTrue(theRule.TryCreateContinuation(new DivideByZeroException(), theEnvelope,
            out var continuation));

        ShouldBeTestExtensions.ShouldBe<IContinuation>(continuation, RequeueContinuation.Instance);
    }

    [Fact]
    public void matches_second_attempt()
    {
        theRule.AddSlot(RequeueContinuation.Instance);
        theRule.AddSlot(RetryInlineContinuation.Instance);

        theEnvelope.Attempts = 2;

        ShouldBeBooleanExtensions.ShouldBeTrue(theRule.TryCreateContinuation(new DivideByZeroException(), theEnvelope,
            out var continuation));

        ShouldBeTestExtensions.ShouldBe<IContinuation>(continuation, RetryInlineContinuation.Instance);
    }

    [Fact]
    public void exceeds_known_slots_and_should_be_dead_letter_queued()
    {
        theRule.AddSlot(RequeueContinuation.Instance);
        theRule.AddSlot(RetryInlineContinuation.Instance);

        // This exceeds the known usages
        theEnvelope.Attempts = 3;

        ShouldBeBooleanExtensions.ShouldBeTrue(theRule.TryCreateContinuation(new DivideByZeroException(), theEnvelope,
            out var continuation));

        ShouldBeTestExtensions.ShouldBeOfType<MoveToErrorQueue>(continuation);
    }
}
