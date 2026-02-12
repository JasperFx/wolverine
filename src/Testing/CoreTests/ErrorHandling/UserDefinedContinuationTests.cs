using System.Diagnostics;
using Wolverine.ErrorHandling;
using Wolverine.Runtime;
using Xunit;

namespace CoreTests.ErrorHandling;

public class UserDefinedContinuationTests
{
    [Fact]
    public void build_can_be_overridden_to_return_custom_continuation()
    {
        var exception = new InvalidOperationException("bad");
        var envelope = new Envelope();
        var continuation = new OverridableContinuation();
        UserDefinedContinuation baseContinuation = continuation;

        var built = baseContinuation.Build(exception, envelope);

        built.ShouldBeOfType<CustomContinuation>();
        continuation.BuildWasCalled.ShouldBeTrue();
        continuation.SeenException.ShouldBeSameAs(exception);
        continuation.SeenEnvelope.ShouldBeSameAs(envelope);
    }

    [Fact]
    public void build_uses_virtual_dispatch_through_interface()
    {
        var exception = new InvalidOperationException("bad");
        var envelope = new Envelope();
        var continuation = new OverridableContinuation();
        IContinuationSource source = continuation;

        var built = source.Build(exception, envelope);

        built.ShouldBeOfType<CustomContinuation>();
        continuation.BuildWasCalled.ShouldBeTrue();
    }
    
    private sealed class OverridableContinuation : UserDefinedContinuation
    {
        public OverridableContinuation() : base("Test override")
        {
        }

        public bool BuildWasCalled { get; private set; }
        public Exception? SeenException { get; private set; }
        public Envelope? SeenEnvelope { get; private set; }

        public override IContinuation Build(Exception ex, Envelope envelope)
        {
            BuildWasCalled = true;
            SeenException = ex;
            SeenEnvelope = envelope;
            return new CustomContinuation();
        }

        public override ValueTask ExecuteAsync(IEnvelopeLifecycle lifecycle, IWolverineRuntime runtime,
            DateTimeOffset now, Activity? activity)
        {
            return ValueTask.CompletedTask;
        }
    }

    private sealed class CustomContinuation : IContinuation
    {
        public ValueTask ExecuteAsync(IEnvelopeLifecycle lifecycle, IWolverineRuntime runtime, DateTimeOffset now,
            Activity? activity)
        {
            return ValueTask.CompletedTask;
        }
    }
}
