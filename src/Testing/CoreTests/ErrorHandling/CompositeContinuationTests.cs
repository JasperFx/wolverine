using System;
using System.Threading.Tasks;
using CoreTests.Runtime;
using NSubstitute;
using NSubstitute.ExceptionExtensions;
using Wolverine.ErrorHandling;
using Wolverine.Runtime;
using Xunit;

namespace CoreTests.ErrorHandling;

public class CompositeContinuationTests
{
    [Fact]
    public async ValueTask executes_all_continuations()
    {
        var inner1 = Substitute.For<IContinuation>();
        var inner2 = Substitute.For<IContinuation>();

        var continuation = new CompositeContinuation(inner1, inner2);

        var context = Substitute.For<IMessageContext>();
        var runtime = new MockWolverineRuntime();
        var now = DateTimeOffset.UtcNow;

        await continuation.ExecuteAsync(context, runtime, now);

        await inner1.Received().ExecuteAsync(context, runtime, now);
        await inner2.Received().ExecuteAsync(context, runtime, now);
    }

    [Fact]
    public async ValueTask executes_all_continuations_even_on_failures()
    {
        var inner1 = Substitute.For<IContinuation>();
        var inner2 = Substitute.For<IContinuation>();

        var continuation = new CompositeContinuation(inner1, inner2);

        var context = Substitute.For<IMessageContext>();
        var runtime = new MockWolverineRuntime();
        var now = DateTimeOffset.UtcNow;

        inner1.ExecuteAsync(context, runtime, now).Throws(new DivideByZeroException());

        await continuation.ExecuteAsync(context, runtime, now);

        await inner2.Received().ExecuteAsync(context, runtime, now);
    }
}
