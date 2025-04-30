using System.Diagnostics;
using CoreTests.Runtime;
using NSubstitute;
using NSubstitute.ExceptionExtensions;
using Wolverine.ErrorHandling;
using Wolverine.Runtime;
using Wolverine.Runtime.Handlers;
using Xunit;

namespace CoreTests.ErrorHandling;

public class CompositeContinuationTests
{
    [Fact]
    public async Task executes_all_continuations()
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
    public async Task executes_all_continuations_even_on_failures()
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

    [Fact]
    public async Task execute_as_inline_not_inline_continuations()
    {
        var inner1 = Substitute.For<IContinuation>();
        var inner2 = Substitute.For<IContinuation>();

        var lifecycle = Substitute.For<IEnvelopeLifecycle>();
        var runtime = new MockWolverineRuntime();
        var now = DateTimeOffset.UtcNow;

        var activity = new Activity("process");
        
        var continuation = new CompositeContinuation(inner1, inner2);
        (await continuation.ExecuteInlineAsync(lifecycle, runtime, now, activity, CancellationToken.None))
            .ShouldBe(InvokeResult.Stop);
    }

    [Fact]
    public async Task execute_as_inline_one_inline()
    {
        var inner1 = Substitute.For<IContinuation>();
        var inner2 = new FakeInlineContinuation(InvokeResult.TryAgain);
        
        var lifecycle = Substitute.For<IEnvelopeLifecycle>();
        var runtime = new MockWolverineRuntime();
        var now = DateTimeOffset.UtcNow;

        var activity = new Activity("process");
        
        var continuation = new CompositeContinuation(inner1, inner2);
        (await continuation.ExecuteInlineAsync(lifecycle, runtime, now, activity, CancellationToken.None))
            .ShouldBe(inner2.Result);
        
        inner2.WasExecuted.ShouldBeTrue();
    }
    
    [Fact]
    public async Task execute_as_inline_multiple_inlines_all_say_try_again()
    {
        var inner1 = new FakeInlineContinuation(InvokeResult.TryAgain);
        var inner2 = new FakeInlineContinuation(InvokeResult.TryAgain);
        
        var lifecycle = Substitute.For<IEnvelopeLifecycle>();
        var runtime = new MockWolverineRuntime();
        var now = DateTimeOffset.UtcNow;

        var activity = new Activity("process");
        
        var continuation = new CompositeContinuation(inner1, inner2);
        (await continuation.ExecuteInlineAsync(lifecycle, runtime, now, activity, CancellationToken.None))
            .ShouldBe(InvokeResult.TryAgain);
        
        inner1.WasExecuted.ShouldBeTrue();
        inner2.WasExecuted.ShouldBeTrue();
    }
    
    [Fact]
    public async Task execute_as_inline_multiple_inlines_ssome_say_stop()
    {
        var inner1 = new FakeInlineContinuation(InvokeResult.TryAgain);
        var inner2 = new FakeInlineContinuation(InvokeResult.Stop);
        
        var lifecycle = Substitute.For<IEnvelopeLifecycle>();
        var runtime = new MockWolverineRuntime();
        var now = DateTimeOffset.UtcNow;

        var activity = new Activity("process");
        
        var continuation = new CompositeContinuation(inner1, inner2);
        (await continuation.ExecuteInlineAsync(lifecycle, runtime, now, activity, CancellationToken.None))
            .ShouldBe(InvokeResult.Stop);
        
        inner1.WasExecuted.ShouldBeTrue();
        inner2.WasExecuted.ShouldBeTrue();
    }
}

public class FakeInlineContinuation : IInlineContinuation, IContinuation
{
    public InvokeResult Result { get; }

    public FakeInlineContinuation(InvokeResult result)
    {
        Result = result;
    }

    public ValueTask<InvokeResult> ExecuteInlineAsync(IEnvelopeLifecycle lifecycle, IWolverineRuntime runtime, DateTimeOffset now,
        Activity? activity, CancellationToken cancellation)
    {
        WasExecuted = true;
        return new ValueTask<InvokeResult>(Result);
    }

    public bool WasExecuted { get; set; }

    public ValueTask ExecuteAsync(IEnvelopeLifecycle lifecycle, IWolverineRuntime runtime, DateTimeOffset now, Activity? activity)
    {
        return new ValueTask();
    }
}