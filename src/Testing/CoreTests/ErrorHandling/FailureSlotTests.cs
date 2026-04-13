using NSubstitute;
using Shouldly;
using Wolverine.ComplianceTests;
using Wolverine.ErrorHandling;
using Wolverine.Runtime;
using Xunit;

namespace CoreTests.ErrorHandling;

public class FailureSlotTests
{
    [Fact]
    public void build_for_one_source()
    {
        var continuation = Substitute.For<IContinuation>();
        var source = Substitute.For<IContinuationSource>();
        var ex = new DivideByZeroException();
        var env = ObjectMother.Envelope();

        source.Build(ex, env).Returns(continuation);

        var slot = new FailureSlot(3, source);

        slot.Build(ex, env).ShouldBe(continuation);
    }

    [Fact]
    public void build_for_multiple_sources()
    {
        var ex = new DivideByZeroException();
        var env = ObjectMother.Envelope();

        var continuation1 = Substitute.For<IContinuation>();
        var source1 = Substitute.For<IContinuationSource>();
        source1.Build(ex, env).Returns(continuation1);

        var continuation2 = Substitute.For<IContinuation>();
        var source2 = Substitute.For<IContinuationSource>();
        source2.Build(ex, env).Returns(continuation2);

        var slot = new FailureSlot(3, source1);
        slot.AddAdditionalSource(source2);

        var continuation = slot.Build(ex, env).ShouldBeOfType<CompositeContinuation>();

        continuation.Inner[0].ShouldBe(continuation1);
        continuation.Inner[1].ShouldBe(continuation2);
    }

    [Fact]
    public void apply_jitter_returns_true_when_source_accepts_strategy()
    {
        var continuation = new RetryInlineContinuation(TimeSpan.FromSeconds(1));
        var slot = new FailureSlot(1, continuation);

        slot.ApplyJitter(new FullJitter()).ShouldBeTrue();
    }

    [Fact]
    public void apply_jitter_returns_false_when_no_jitterable_source()
    {
        var source = Substitute.For<IContinuationSource>();
        var slot = new FailureSlot(1, source);

        slot.ApplyJitter(new FullJitter()).ShouldBeFalse();
    }
}