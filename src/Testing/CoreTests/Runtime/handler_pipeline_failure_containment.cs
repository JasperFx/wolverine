using System;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using NSubstitute;
using Shouldly;
using Wolverine;
using Wolverine.Logging;
using Wolverine.Runtime;
using Wolverine.Transports;
using Xunit;

namespace CoreTests.Runtime;

// GH-3111: a single un-recoverable envelope (classically an OutOfMemoryException from an unbounded
// deserialization) must not be able to take the host down. The pipeline's last-resort recovery
// (ack the message out of the way + log) itself allocates, so when the original failure is memory
// exhaustion it can re-throw. If that escapes InvokeAsync it faults the receiver loop, stops the
// listener, and the host exits cleanly (code 0) into a broker redelivery / crash loop. These tests
// pin the containment: RecoverFromFailedProcessingAsync never throws, whatever the recovery path does.
public class handler_pipeline_failure_containment
{
    private readonly Envelope theEnvelope = new() { Id = Guid.NewGuid() };
    private readonly Exception theOriginalFailure = new OutOfMemoryException("deserialization blew the heap");
    private readonly IMessageTracker theTracker = Substitute.For<IMessageTracker>();

    [Fact]
    public async Task happy_path_acks_the_message_and_logs_the_exception()
    {
        var channel = new FakeChannel();
        var logger = new RecordingLogger();

        await Should.NotThrowAsync(() => HandlerPipeline
            .RecoverFromFailedProcessingAsync(channel, theEnvelope, theOriginalFailure, theTracker, logger, null)
            .AsTask());

        channel.CompleteCalls.ShouldBe(1);
        theTracker.Received(1).LogException(theOriginalFailure, theEnvelope.Id);
        // No containment error logged when recovery succeeds.
        logger.ErrorCalls.ShouldBe(0);
    }

    [Fact]
    public async Task contains_oom_thrown_by_complete_async()
    {
        var channel = new FakeChannel { CompleteThrows = new OutOfMemoryException("ack also OOMs") };
        var logger = new RecordingLogger();

        await Should.NotThrowAsync(() => HandlerPipeline
            .RecoverFromFailedProcessingAsync(channel, theEnvelope, theOriginalFailure, theTracker, logger, null)
            .AsTask());

        // The recovery failure is contained and reported, not rethrown.
        logger.ErrorCalls.ShouldBe(1);
    }

    [Fact]
    public async Task contains_oom_thrown_while_logging_the_original_exception()
    {
        var channel = new FakeChannel();
        theTracker.When(t => t.LogException(Arg.Any<Exception>(), Arg.Any<object?>(), Arg.Any<string>()))
            .Do(_ => throw new OutOfMemoryException("structured logging OOMs"));
        var logger = new RecordingLogger();

        await Should.NotThrowAsync(() => HandlerPipeline
            .RecoverFromFailedProcessingAsync(channel, theEnvelope, theOriginalFailure, theTracker, logger, null)
            .AsTask());

        channel.CompleteCalls.ShouldBe(1);
        logger.ErrorCalls.ShouldBe(1);
    }

    [Fact]
    public async Task never_throws_even_when_the_contained_logging_itself_throws()
    {
        // Worst case at the true memory ceiling: even the minimal containment log allocates and fails.
        var channel = new FakeChannel { CompleteThrows = new OutOfMemoryException() };
        var logger = new ThrowingLogger();

        await Should.NotThrowAsync(() => HandlerPipeline
            .RecoverFromFailedProcessingAsync(channel, theEnvelope, theOriginalFailure, theTracker, logger, null)
            .AsTask());
    }

    private sealed class FakeChannel : IChannelCallback
    {
        public Exception? CompleteThrows { get; init; }
        public int CompleteCalls { get; private set; }

        public IHandlerPipeline? Pipeline => null;

        public ValueTask CompleteAsync(Envelope envelope)
        {
            CompleteCalls++;
            if (CompleteThrows != null) throw CompleteThrows;
            return ValueTask.CompletedTask;
        }

        public ValueTask DeferAsync(Envelope envelope) => ValueTask.CompletedTask;
    }

    private sealed class RecordingLogger : ILogger
    {
        public int ErrorCalls { get; private set; }

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            if (logLevel >= LogLevel.Error) ErrorCalls++;
        }
    }

    private sealed class ThrowingLogger : ILogger
    {
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception,
            Func<TState, Exception?, string> formatter)
            => throw new OutOfMemoryException("even logging OOMs");
    }
}
