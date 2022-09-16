using System;
using CoreTests.Messaging;
using NSubstitute;
using Shouldly;
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

        ShouldBeTestExtensions.ShouldBe(slot.Build(ex, env), continuation);
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
}
