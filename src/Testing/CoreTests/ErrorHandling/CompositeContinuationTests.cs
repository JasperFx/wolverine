using System;
using System.Diagnostics;
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

        var lifecycle = Substitute.For<IEnvelopeLifecycle>();
        var runtime = new MockWolverineRuntime();
        var now = DateTimeOffset.UtcNow;

        var activity = new Activity("process");
        
        await continuation.ExecuteAsync(lifecycle, runtime, now, activity);

        await inner1.Received().ExecuteAsync(lifecycle, runtime, now, activity);
        await inner2.Received().ExecuteAsync(lifecycle, runtime, now, activity);
    }

    [Fact]
    public async ValueTask executes_all_continuations_even_on_failures()
    {
        var inner1 = Substitute.For<IContinuation>();
        var inner2 = Substitute.For<IContinuation>();

        var continuation = new CompositeContinuation(inner1, inner2);

        var lifecycle = Substitute.For<IEnvelopeLifecycle>();
        var runtime = new MockWolverineRuntime();
        var now = DateTimeOffset.UtcNow;

        var activity = new Activity("process");
        inner1.ExecuteAsync(lifecycle, runtime, now, activity).Throws(new DivideByZeroException());

        await continuation.ExecuteAsync(lifecycle, runtime, now, activity);

        await inner2.Received().ExecuteAsync(lifecycle, runtime, now, activity);
    }
}