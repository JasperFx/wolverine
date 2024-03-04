using JasperFx.Core;
using TestingSupport;
using Wolverine.ErrorHandling;
using Wolverine.Runtime.Handlers;
using Xunit;

namespace CoreTests.ErrorHandling;

public class FailureRuleCollectionTests
{
    private readonly Envelope theEnvelope = ObjectMother.Envelope();
    private readonly HandlerGraph theHandlers = new();

    [Fact]
    public void match_on_exception_type()
    {
        theHandlers.OnException<DivideByZeroException>().Discard();
        theHandlers.OnException<BadImageFormatException>().Requeue();

        theHandlers.Failures.DetermineExecutionContinuation(new DivideByZeroException(), theEnvelope)
            .ShouldBeOfType<DiscardEnvelope>();

        theHandlers.Failures.DetermineExecutionContinuation(new BadImageFormatException(), theEnvelope)
            .ShouldBeOfType<RequeueContinuation>();
    }

    [Fact]
    public void match_on_exception_type_2()
    {
        theHandlers.OnExceptionOfType(typeof(DivideByZeroException)).Discard();
        theHandlers.OnExceptionOfType(typeof(BadImageFormatException)).Requeue();

        theHandlers.Failures.DetermineExecutionContinuation(new DivideByZeroException(), theEnvelope)
            .ShouldBeOfType<DiscardEnvelope>();

        theHandlers.Failures.DetermineExecutionContinuation(new BadImageFormatException(), theEnvelope)
            .ShouldBeOfType<RequeueContinuation>();
    }

    [Fact]
    public void match_on_exception_predicate()
    {
        theHandlers.OnException(e => e is DivideByZeroException).Discard();
        theHandlers.OnException(e => e is BadImageFormatException).Requeue();

        theHandlers.Failures.DetermineExecutionContinuation(new DivideByZeroException(), theEnvelope)
            .ShouldBeOfType<DiscardEnvelope>();

        theHandlers.Failures.DetermineExecutionContinuation(new BadImageFormatException(), theEnvelope)
            .ShouldBeOfType<RequeueContinuation>();
    }

    [Fact]
    public void filter_by_exception_type_and_func()
    {
        theHandlers.OnException<CodeException>(e => e.Code == 1)
            .Discard();

        theHandlers.OnException<CodeException>(e => e.Code == 2)
            .Requeue();

        theHandlers.Failures.DetermineExecutionContinuation(new CodeException { Code = 1 }, theEnvelope)
            .ShouldBeOfType<DiscardEnvelope>();

        theHandlers.Failures.DetermineExecutionContinuation(new CodeException { Code = 2 }, theEnvelope)
            .ShouldBeOfType<RequeueContinuation>();
    }

    [Fact]
    public void match_on_inner_exception()
    {
        theHandlers.OnInnerException<DivideByZeroException>().Discard();
        theHandlers.OnInnerException<BadImageFormatException>().Requeue();

        theHandlers.Failures
            .DetermineExecutionContinuation(new Exception("bad", new DivideByZeroException()), theEnvelope)
            .ShouldBeOfType<DiscardEnvelope>();

        theHandlers.Failures
            .DetermineExecutionContinuation(new Exception("worse", new BadImageFormatException()), theEnvelope)
            .ShouldBeOfType<RequeueContinuation>();
    }

    [Fact]
    public void match_on_inner_exception_and_predicate()
    {
        theHandlers.OnInnerException<CodeException>(e => e.Code == 1).Discard();
        theHandlers.OnInnerException<CodeException>(e => e.Code == 2).Requeue();

        theHandlers.Failures
            .DetermineExecutionContinuation(new Exception("bad", new CodeException { Code = 1 }), theEnvelope)
            .ShouldBeOfType<DiscardEnvelope>();

        theHandlers.Failures
            .DetermineExecutionContinuation(new Exception("worse", new CodeException { Code = 2 }), theEnvelope)
            .ShouldBeOfType<RequeueContinuation>();
    }

    [Fact]
    public void and_message_contains_match()
    {
        theHandlers.OnException<DivideByZeroException>().AndMessageContains("boom").Discard();
        theHandlers.OnException<DivideByZeroException>().Requeue();

        theHandlers.Failures.DetermineExecutionContinuation(new DivideByZeroException("boom"), theEnvelope)
            .ShouldBeOfType<DiscardEnvelope>();

        theHandlers.Failures
            .DetermineExecutionContinuation(new DivideByZeroException("big boom in the room"), theEnvelope)
            .ShouldBeOfType<DiscardEnvelope>();

        theHandlers.Failures
            .DetermineExecutionContinuation(new DivideByZeroException("big BOOM in the room"), theEnvelope)
            .ShouldBeOfType<DiscardEnvelope>();

        theHandlers.Failures.DetermineExecutionContinuation(new DivideByZeroException("wrong text"), theEnvelope)
            .ShouldBeOfType<RequeueContinuation>();
    }

    [Fact]
    public void or_exception_type()
    {
        theHandlers.OnException<DivideByZeroException>().Or<BadImageFormatException>().Discard();
        theHandlers.OnException(x => true).Requeue();

        theHandlers.Failures.DetermineExecutionContinuation(new DivideByZeroException(), theEnvelope)
            .ShouldBeOfType<DiscardEnvelope>();

        theHandlers.Failures.DetermineExecutionContinuation(new BadImageFormatException(), theEnvelope)
            .ShouldBeOfType<DiscardEnvelope>();
    }

    [Fact]
    public void or_predicate()
    {
        theHandlers.OnException<DivideByZeroException>().Or(e => e is BadImageFormatException).Discard();
        theHandlers.OnException(x => true).Requeue();

        theHandlers.Failures.DetermineExecutionContinuation(new DivideByZeroException(), theEnvelope)
            .ShouldBeOfType<DiscardEnvelope>();

        theHandlers.Failures.DetermineExecutionContinuation(new BadImageFormatException(), theEnvelope)
            .ShouldBeOfType<DiscardEnvelope>();
    }

    [Fact]
    public void or_predicate_on_specific_exception_type()
    {
        theHandlers.OnException<DivideByZeroException>().Or<CodeException>(e => e.Code == 1).Discard();
        theHandlers.OnException(x => true).Requeue();

        theHandlers.Failures.DetermineExecutionContinuation(new DivideByZeroException(), theEnvelope)
            .ShouldBeOfType<DiscardEnvelope>();

        theHandlers.Failures.DetermineExecutionContinuation(new CodeException { Code = 1 }, theEnvelope);
        theHandlers.Failures.DetermineExecutionContinuation(new CodeException { Code = 2 }, theEnvelope)
            .ShouldBeOfType<RequeueContinuation>();
    }

    [Fact]
    public void or_predicate_on_inner_type()
    {
        theHandlers.OnException<DivideByZeroException>().OrInner<CodeException>().Discard();
        theHandlers.OnException(x => true).Requeue();

        theHandlers.Failures.DetermineExecutionContinuation(new DivideByZeroException(), theEnvelope)
            .ShouldBeOfType<DiscardEnvelope>();

        theHandlers.Failures.DetermineExecutionContinuation(new Exception("bad", new CodeException { Code = 1 }),
            theEnvelope);

        theHandlers.Failures
            .DetermineExecutionContinuation(new Exception("worse", new CodeException { Code = 2 }), theEnvelope)
            .ShouldBeOfType<RequeueContinuation>();

        theHandlers.Failures.DetermineExecutionContinuation(new BadImageFormatException(), theEnvelope)
            .ShouldBeOfType<RequeueContinuation>();

        theHandlers.Failures.DetermineExecutionContinuation(new CodeException(), theEnvelope)
            .ShouldBeOfType<RequeueContinuation>();
    }

    [Fact]
    public void or_predicate_on_inner_type_with_predicate()
    {
        theHandlers.OnException<DivideByZeroException>().OrInner<CodeException>(e => e.Code == 1).Discard();
        theHandlers.OnException(x => true).Requeue();

        theHandlers.Failures.DetermineExecutionContinuation(new DivideByZeroException(), theEnvelope)
            .ShouldBeOfType<DiscardEnvelope>();

        theHandlers.Failures.DetermineExecutionContinuation(new Exception("bad", new CodeException { Code = 1 }),
            theEnvelope);
        theHandlers.Failures
            .DetermineExecutionContinuation(new Exception("worse", new CodeException { Code = 2 }), theEnvelope)
            .ShouldBeOfType<RequeueContinuation>();

        theHandlers.Failures.DetermineExecutionContinuation(new BadImageFormatException(), theEnvelope)
            .ShouldBeOfType<RequeueContinuation>();

        theHandlers.Failures.DetermineExecutionContinuation(new CodeException(), theEnvelope)
            .ShouldBeOfType<RequeueContinuation>();
    }

    [Fact]
    public void move_to_error_queue()
    {
        theHandlers.OnException<DivideByZeroException>().MoveToErrorQueue();
        theHandlers.OnException<BadImageFormatException>().Requeue();

        theHandlers.Failures.DetermineExecutionContinuation(new DivideByZeroException(), theEnvelope)
            .ShouldBeOfType<MoveToErrorQueue>();
    }

    [Fact]
    public void requeue_n_times()
    {
        theHandlers.OnException<BadImageFormatException>().Requeue();

        theEnvelope.Attempts = 0;
        theHandlers.Failures.DetermineExecutionContinuation(new BadImageFormatException(), theEnvelope)
            .ShouldBeOfType<RequeueContinuation>();

        theEnvelope.Attempts = 1;
        theHandlers.Failures.DetermineExecutionContinuation(new BadImageFormatException(), theEnvelope)
            .ShouldBeOfType<RequeueContinuation>();

        theEnvelope.Attempts = 2;
        theHandlers.Failures.DetermineExecutionContinuation(new BadImageFormatException(), theEnvelope)
            .ShouldBeOfType<RequeueContinuation>();

        theEnvelope.Attempts = 3;
        theHandlers.Failures.DetermineExecutionContinuation(new BadImageFormatException(), theEnvelope)
            .ShouldBeOfType<MoveToErrorQueue>();
    }

    [Fact]
    public void schedule_retry_n_times()
    {
        theHandlers.OnException<BadImageFormatException>().ScheduleRetry(1.Minutes(), 5.Minutes(), 10.Minutes());

        theEnvelope.Attempts = 0;
        theHandlers.Failures.DetermineExecutionContinuation(new BadImageFormatException(), theEnvelope)
            .ShouldBeOfType<ScheduledRetryContinuation>().Delay.ShouldBe(1.Minutes());

        theEnvelope.Attempts = 1;
        theHandlers.Failures.DetermineExecutionContinuation(new BadImageFormatException(), theEnvelope)
            .ShouldBeOfType<ScheduledRetryContinuation>().Delay.ShouldBe(1.Minutes());

        theEnvelope.Attempts = 2;
        theHandlers.Failures.DetermineExecutionContinuation(new BadImageFormatException(), theEnvelope)
            .ShouldBeOfType<ScheduledRetryContinuation>().Delay.ShouldBe(5.Minutes());

        theEnvelope.Attempts = 3;
        theHandlers.Failures.DetermineExecutionContinuation(new BadImageFormatException(), theEnvelope)
            .ShouldBeOfType<ScheduledRetryContinuation>().Delay.ShouldBe(10.Minutes());

        theEnvelope.Attempts = 4;
        theHandlers.Failures.DetermineExecutionContinuation(new BadImageFormatException(), theEnvelope)
            .ShouldBeOfType<MoveToErrorQueue>();
    }

    [Fact]
    public void retry_n_times()
    {
        theHandlers.OnException<BadImageFormatException>().RetryTimes(3);

        theEnvelope.Attempts = 0;
        theHandlers.Failures.DetermineExecutionContinuation(new BadImageFormatException(), theEnvelope)
            .ShouldBeOfType<RetryInlineContinuation>().Delay.ShouldBeNull();

        theEnvelope.Attempts = 1;
        theHandlers.Failures.DetermineExecutionContinuation(new BadImageFormatException(), theEnvelope)
            .ShouldBeOfType<RetryInlineContinuation>().Delay.ShouldBeNull();

        theEnvelope.Attempts = 2;
        theHandlers.Failures.DetermineExecutionContinuation(new BadImageFormatException(), theEnvelope)
            .ShouldBeOfType<RetryInlineContinuation>().Delay.ShouldBeNull();

        theEnvelope.Attempts = 3;
        theHandlers.Failures.DetermineExecutionContinuation(new BadImageFormatException(), theEnvelope)
            .ShouldBeOfType<RetryInlineContinuation>();

        theEnvelope.Attempts = 4;
        theHandlers.Failures.DetermineExecutionContinuation(new BadImageFormatException(), theEnvelope)
            .ShouldBeOfType<MoveToErrorQueue>();
    }

    [Fact]
    public void retry_with_cooldown_n_times()
    {
        theHandlers.OnException<BadImageFormatException>().RetryWithCooldown(1.Minutes(), 5.Minutes(), 10.Minutes());

        theEnvelope.Attempts = 0;
        theHandlers.Failures.DetermineExecutionContinuation(new BadImageFormatException(), theEnvelope)
            .ShouldBeOfType<RetryInlineContinuation>().Delay.ShouldBe(1.Minutes());

        theEnvelope.Attempts = 1;
        theHandlers.Failures.DetermineExecutionContinuation(new BadImageFormatException(), theEnvelope)
            .ShouldBeOfType<RetryInlineContinuation>().Delay.ShouldBe(1.Minutes());

        theEnvelope.Attempts = 2;
        theHandlers.Failures.DetermineExecutionContinuation(new BadImageFormatException(), theEnvelope)
            .ShouldBeOfType<RetryInlineContinuation>().Delay.ShouldBe(5.Minutes());

        theEnvelope.Attempts = 3;
        theHandlers.Failures.DetermineExecutionContinuation(new BadImageFormatException(), theEnvelope)
            .ShouldBeOfType<RetryInlineContinuation>().Delay.ShouldBe(10.Minutes());

        theEnvelope.Attempts = 4;
        theHandlers.Failures.DetermineExecutionContinuation(new BadImageFormatException(), theEnvelope)
            .ShouldBeOfType<MoveToErrorQueue>();
    }

    [Fact]
    public void scripted_order_of_continuations()
    {
        theHandlers.OnException<BadImageFormatException>()
            .RetryOnce()
            .Then.RetryWithCooldown(1.Seconds())
            .Then.ScheduleRetry(1.Minutes());

        theEnvelope.Attempts = 0;
        theHandlers.Failures.DetermineExecutionContinuation(new BadImageFormatException(), theEnvelope)
            .ShouldBeOfType<RetryInlineContinuation>().Delay.ShouldBeNull();

        theEnvelope.Attempts = 1;
        theHandlers.Failures.DetermineExecutionContinuation(new BadImageFormatException(), theEnvelope)
            .ShouldBeOfType<RetryInlineContinuation>().Delay.ShouldBeNull();

        theEnvelope.Attempts = 2;
        theHandlers.Failures.DetermineExecutionContinuation(new BadImageFormatException(), theEnvelope)
            .ShouldBeOfType<RetryInlineContinuation>().Delay.ShouldBe(1.Seconds());

        theEnvelope.Attempts = 3;
        theHandlers.Failures.DetermineExecutionContinuation(new BadImageFormatException(), theEnvelope)
            .ShouldBeOfType<ScheduledRetryContinuation>().Delay.ShouldBe(1.Minutes());

        theEnvelope.Attempts = 4;
        theHandlers.Failures.DetermineExecutionContinuation(new BadImageFormatException(), theEnvelope)
            .ShouldBeOfType<MoveToErrorQueue>();
    }

    [Fact]
    public void requeue_and_pause()
    {
        theHandlers.OnException<BadImageFormatException>().Requeue().AndPauseProcessing(5.Minutes());

        var composite = theHandlers.Failures.DetermineExecutionContinuation(new BadImageFormatException(), theEnvelope)
            .ShouldBeOfType<CompositeContinuation>();

        composite.Inner[0].ShouldBeOfType<RequeueContinuation>();
        composite.Inner[1].ShouldBeOfType<PauseListenerContinuation>()
            .PauseTime.ShouldBe(5.Minutes());
    }
}

public class CodeException : Exception
{
    public int Code { get; set; }
}