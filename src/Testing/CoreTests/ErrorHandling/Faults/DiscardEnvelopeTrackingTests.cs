using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Wolverine;
using Wolverine.ErrorHandling;
using Wolverine.Logging;
using Wolverine.Runtime;
using Xunit;

namespace CoreTests.ErrorHandling.Faults;

public class DiscardEnvelopeTrackingTests
{
    private static (IEnvelopeLifecycle lifecycle, Envelope envelope, IWolverineRuntime runtime,
        IMessageTracker tracker, ILogger logger)
        CreateRuntime()
    {
        var lifecycle = Substitute.For<IEnvelopeLifecycle>();
        var envelope = new Envelope { Id = Guid.NewGuid(), Source = "tests" };
        lifecycle.Envelope.Returns(envelope);

        var tracker = Substitute.For<IMessageTracker>();
        var logger = Substitute.For<ILogger>();

        // Plain IWolverineRuntime substitute is not IWolverineRuntimeInternal, so
        // PublishFaultIfEnabledAsync becomes a no-op (returns ValueTask.CompletedTask).
        // That is intentional: this test is only about the tracking-on-throw contract.
        var runtime = Substitute.For<IWolverineRuntime>();
        runtime.MessageTracking.Returns(tracker);
        runtime.Logger.Returns(logger);

        return (lifecycle, envelope, runtime, tracker, logger);
    }

    [Fact]
    public async Task tracking_event_fires_when_complete_async_throws()
    {
        var (lifecycle, envelope, runtime, tracker, logger) = CreateRuntime();
        lifecycle
            .When(x => x.CompleteAsync())
            .Do(_ => throw new InvalidOperationException("transient broker commit failure"));

        var continuation = new DiscardEnvelope(new Exception("original"));

        // Should not propagate.
        await continuation.ExecuteAsync(lifecycle, runtime, DateTimeOffset.UtcNow, activity: null);

        tracker.Received(1).DiscardedEnvelope(envelope);
        logger.Received().Log(
            LogLevel.Error,
            Arg.Any<EventId>(),
            Arg.Any<object>(),
            Arg.Any<InvalidOperationException>(),
            Arg.Any<Func<object, Exception?, string>>());
    }

    [Fact]
    public async Task tracking_event_fires_exactly_once_on_success()
    {
        var (lifecycle, envelope, runtime, tracker, _) = CreateRuntime();
        runtime.Logger.Returns(NullLogger.Instance);

        var continuation = new DiscardEnvelope(new Exception());

        await continuation.ExecuteAsync(lifecycle, runtime, DateTimeOffset.UtcNow, activity: null);

        tracker.Received(1).DiscardedEnvelope(envelope);
    }
}
