using System.Diagnostics;
using JasperFx.Core;
using Microsoft.Extensions.Hosting;
using Shouldly;
using Wolverine;
using Wolverine.ErrorHandling;
using Wolverine.Runtime;
using Wolverine.Runtime.WorkerQueues;
using Wolverine.Tracking;
using Wolverine.Transports;
using Wolverine.Transports.Stub;
using Wolverine.Util;
using Xunit;

namespace CoreTests.Runtime;

/// <summary>
/// GH-5547. An inline retry runs the next attempt from inside the failed attempt's error handling. The failed
/// attempt's span is stopped -- with its error status and its "exception" event already on it -- before the next
/// attempt starts, so it no longer temporally contains the retries after it and the dead letter move at the end.
/// The next attempt is linked to it (GH-4398), not parented by it. An inline listener's receive span is a different
/// thing: it is meant to contain everything done for the delivery, and it is left alone.
/// </summary>
public class inline_retry_spans_5547
{
    // A constant rather than theAmbientSource.Name: a listener added before this class's static constructor
    // runs is asked about the source while the source is still being constructed
    private const string AmbientSourceName = "CoreTests.gh-5547";
    private static readonly ActivitySource theAmbientSource = new(AmbientSourceName);

    private static async Task<(IHost host, SpanRecorder recorder)> startAsync()
    {
        var recorder = new SpanRecorder();

        var host = await Host.CreateDefaultBuilder()
            .UseWolverine(opts =>
            {
                opts.OnException<RetryOnce5547>().RetryOnce().Then.MoveToErrorQueue();
                opts.OnException<RetryWithCooldown5547>()
                    .RetryWithCooldown(5.Milliseconds(), 5.Milliseconds()).Then.MoveToErrorQueue();
                opts.OnException<DoThenRetry5547>()
                    .DoThenRetry((_, _, _) => ValueTask.CompletedTask, maxAttempts: 2).Then.MoveToErrorQueue();
            })
            .StartAsync();

        return (host, recorder);
    }

    private static async Task stopAsync(IHost host, SpanRecorder recorder)
    {
        recorder.Dispose();
        await host.StopAsync();
        host.Dispose();
    }

    private static void shouldHaveOneExceptionEvent(Activity span, Exception expected)
    {
        span.Status.ShouldBe(ActivityStatusCode.Error);

        var events = span.Events.Where(x => x.Name == "exception").ToArray();
        events.Length.ShouldBe(1);

        var tags = events[0].Tags.ToDictionary(x => x.Key, x => x.Value);
        tags["exception.type"].ShouldBe(expected.GetType().FullName);
        tags["exception.message"].ShouldBe(expected.Message);
        tags["exception.stacktrace"].ShouldBeOfType<string>().ShouldContain(nameof(InlineRetry5547Handler));
    }

    /// <summary>
    /// What every attempt of a retried, then dead lettered message has to look like: each attempt's span is
    /// stopped before the next one starts, each is linked to the one before it without being its child, each
    /// records its own failure once, and only the last one moved the message to the error queue
    /// </summary>
    private static void shouldBeSeparateLinkedAttempts(SpanRecorder recorder, Activity[] attempts,
        Exception expected)
    {
        for (var i = 1; i < attempts.Length; i++)
        {
            var failed = attempts[i - 1];
            var retry = attempts[i];

            recorder.IndexOf(SpanRecorder.Stopped, failed)
                .ShouldBeLessThan(recorder.IndexOf(SpanRecorder.Started, retry),
                    $"attempt {i} was still open when attempt {i + 1} started");

            retry.Links.ShouldContain(x => x.Context.TraceId == failed.TraceId && x.Context.SpanId == failed.SpanId);
            retry.ParentSpanId.ShouldNotBe(failed.SpanId);
            retry.ParentSpanId.ShouldBe(attempts[0].ParentSpanId);

            failed.Events.ShouldContain(x => x.Name == WolverineTracing.EnvelopeRetry);
            failed.Events.ShouldNotContain(x => x.Name == WolverineTracing.MovedToErrorQueue);
        }

        attempts[^1].Events.ShouldContain(x => x.Name == WolverineTracing.MovedToErrorQueue);

        foreach (var attempt in attempts)
        {
            recorder.StopCount(attempt).ShouldBe(1);
            shouldHaveOneExceptionEvent(attempt, expected);
        }
    }

    [Fact]
    public async Task retry_once_then_dead_letter_stops_each_attempt_before_the_next_starts()
    {
        var (host, recorder) = await startAsync();

        try
        {
            await host.TrackActivity().DoNotAssertOnExceptionsDetected()
                .PublishMessageAndWaitAsync(new RetryOnceMessage5547("order 1 is poison"));

            var attempts = await recorder.StoppedAttemptsAsync<RetryOnceMessage5547>(2);
            attempts.Length.ShouldBe(2);

            shouldBeSeparateLinkedAttempts(recorder, attempts, new RetryOnce5547("order 1 is poison"));
        }
        finally
        {
            await stopAsync(host, recorder);
        }
    }

    [Fact]
    public async Task retry_with_cooldown_stops_each_attempt_before_the_next_starts()
    {
        var (host, recorder) = await startAsync();

        try
        {
            await host.TrackActivity().DoNotAssertOnExceptionsDetected()
                .PublishMessageAndWaitAsync(new RetryWithCooldownMessage5547("order 2 is poison"));

            var attempts = await recorder.StoppedAttemptsAsync<RetryWithCooldownMessage5547>(3);
            attempts.Length.ShouldBe(3);

            shouldBeSeparateLinkedAttempts(recorder, attempts, new RetryWithCooldown5547("order 2 is poison"));
        }
        finally
        {
            await stopAsync(host, recorder);
        }
    }

    [Fact]
    public async Task do_then_retry_stops_the_failed_attempt_before_the_next_starts()
    {
        var (host, recorder) = await startAsync();

        try
        {
            await host.TrackActivity().DoNotAssertOnExceptionsDetected()
                .PublishMessageAndWaitAsync(new DoThenRetryMessage5547("order 3 is poison"));

            var attempts = await recorder.StoppedAttemptsAsync<DoThenRetryMessage5547>(2);
            attempts.Length.ShouldBe(2);

            shouldBeSeparateLinkedAttempts(recorder, attempts, new DoThenRetry5547("order 3 is poison"));
        }
        finally
        {
            await stopAsync(host, recorder);
        }
    }

    /// <summary>
    /// A message with no parent id is parented by whatever is current when its attempt starts. The failed
    /// attempt's span is current while its error handling runs the retry, so the retry has to start from the
    /// failed attempt's own parent, not from the failed attempt
    /// </summary>
    [Fact]
    public async Task a_parentless_retry_is_a_sibling_of_the_failed_attempt_not_its_child()
    {
        var (host, recorder) = await startAsync();

        try
        {
            var envelope = new Envelope(new ParentlessMessage5547("order 4 is poison"))
            {
                Destination = new Uri("stub://inline-retry-5547"),
                ParentId = null
            };

            using (var ambient = theAmbientSource.StartActivity("ambient"))
            {
                ambient.ShouldNotBeNull();

                // Its own async method, so the activities the pipeline makes current never leak back into the test
                await invokeAsync(host.GetRuntime().Pipeline, envelope, new TestListener5547());

                var attempts = recorder.AttemptsFor<ParentlessMessage5547>();
                attempts.Length.ShouldBe(2);

                attempts[0].ParentSpanId.ShouldBe(ambient.SpanId);
                attempts[1].ParentSpanId.ShouldBe(ambient.SpanId);

                shouldBeSeparateLinkedAttempts(recorder, attempts, new RetryOnce5547("order 4 is poison"));
            }
        }
        finally
        {
            await stopAsync(host, recorder);
        }
    }

    private static async Task invokeAsync(IHandlerPipeline pipeline, Envelope envelope, IChannelCallback channel)
    {
        await pipeline.InvokeAsync(envelope, channel);
    }

    /// <summary>
    /// An inline listener hands the pipeline its receive span, which it stops itself and which contains
    /// everything done for the delivery, the retry and the dead letter move included. Only the execution span
    /// started for an attempt is ended early, and an inline listener's first attempt runs in its receive span
    /// </summary>
    [Fact]
    public async Task an_inline_listeners_receive_span_still_contains_the_retry_and_is_stopped_once()
    {
        var (host, recorder) = await startAsync();

        try
        {
            var runtime = host.GetRuntime();
            var receiver = new InlineReceiver(new StubEndpoint("inline-retry-5547", new StubTransport()), runtime,
                runtime.Pipeline);

            var envelope = new Envelope(new InlineListenerMessage5547("order 5 is poison"));

            await receiver.ReceivedAsync(new TestListener5547(), envelope);

            var receive = recorder.ReceiveSpanFor(envelope);
            var retry = recorder.AttemptsFor<InlineListenerMessage5547>().ShouldHaveSingleItem();

            recorder.StopCount(receive).ShouldBe(1);

            recorder.IndexOf(SpanRecorder.Started, receive)
                .ShouldBeLessThan(recorder.IndexOf(SpanRecorder.Started, retry));
            recorder.IndexOf(SpanRecorder.Stopped, retry)
                .ShouldBeLessThan(recorder.IndexOf(SpanRecorder.Stopped, receive));

            retry.Links.ShouldContain(x => x.Context.TraceId == receive.TraceId && x.Context.SpanId == receive.SpanId);
            retry.Events.ShouldContain(x => x.Name == WolverineTracing.MovedToErrorQueue);
            receive.Events.ShouldContain(x => x.Name == WolverineTracing.EnvelopeRetry);

            var expected = new RetryOnce5547("order 5 is poison");
            shouldHaveOneExceptionEvent(receive, expected);
            shouldHaveOneExceptionEvent(retry, expected);
        }
        finally
        {
            await stopAsync(host, recorder);
        }
    }

    /// <summary>
    /// Records every start and stop in the order they happened, which is what "stopped before the next attempt
    /// started" is about. Wall clock timestamps from two activities are not a reliable way to order them.
    /// </summary>
    private sealed class SpanRecorder : IDisposable
    {
        public const string Started = "started";
        public const string Stopped = "stopped";

        private readonly List<(string Kind, Activity Activity)> _events = new();
        private readonly ActivityListener _listener;

        public SpanRecorder()
        {
            _listener = new ActivityListener
            {
                ShouldListenTo = source => source.Name == "Wolverine" || source.Name == AmbientSourceName,
                Sample = (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.AllDataAndRecorded,
                ActivityStarted = activity => record(Started, activity),
                ActivityStopped = activity => record(Stopped, activity)
            };

            ActivitySource.AddActivityListener(_listener);
        }

        private void record(string kind, Activity activity)
        {
            lock (_events)
            {
                _events.Add((kind, activity));
            }
        }

        public int IndexOf(string kind, Activity activity)
        {
            lock (_events)
            {
                var index = _events.FindIndex(x => x.Kind == kind && ReferenceEquals(x.Activity, activity));
                index.ShouldBeGreaterThanOrEqualTo(0, $"no '{kind}' recorded for {activity.OperationName}");
                return index;
            }
        }

        public int StopCount(Activity activity)
        {
            lock (_events)
            {
                return _events.Count(x => x.Kind == Stopped && ReferenceEquals(x.Activity, activity));
            }
        }

        /// <summary>
        /// The stopped attempt spans for one message type, in the order the attempts started
        /// </summary>
        public Activity[] AttemptsFor<T>()
        {
            var name = typeof(T).ToMessageTypeName();
            lock (_events)
            {
                var stopped = _events
                    .Where(x => x.Kind == Stopped && x.Activity.OperationName == name)
                    .Select(x => x.Activity)
                    .Distinct()
                    .ToArray();

                return stopped
                    .OrderBy(a => _events.FindIndex(x => x.Kind == Started && ReferenceEquals(x.Activity, a)))
                    .ToArray();
            }
        }

        /// <summary>
        /// GH-5470 again. A tracked session is done when the message's terminal record arrives, and that is reported
        /// from inside the last attempt's span, which only stops once the pipeline unwinds; the session signals its
        /// completion asynchronously. Wait, boundedly, for the spans to stop. The assertions on how many there are
        /// stay exact.
        /// </summary>
        public async Task<Activity[]> StoppedAttemptsAsync<T>(int expected)
        {
            var timeout = Stopwatch.StartNew();
            var attempts = AttemptsFor<T>();
            while (attempts.Length < expected && timeout.Elapsed < 5.Seconds())
            {
                await Task.Delay(10.Milliseconds());
                attempts = AttemptsFor<T>();
            }

            return attempts;
        }

        public Activity ReceiveSpanFor(Envelope envelope)
        {
            lock (_events)
            {
                // The tag is written as the Guid itself, not its string form
                return _events
                    .Where(x => x.Kind == Stopped && x.Activity.OperationName == "receive" &&
                                Equals(x.Activity.GetTagItem(WolverineTracing.MessagingMessageId), envelope.Id))
                    .Select(x => x.Activity)
                    .Distinct()
                    .ShouldHaveSingleItem();
            }
        }

        public void Dispose()
        {
            _listener.Dispose();
        }
    }
}

internal class TestListener5547 : IListener
{
    public IHandlerPipeline? Pipeline => null;

    public Uri Address { get; } = new("stub://inline-retry-5547");

    public ValueTask CompleteAsync(Envelope envelope) => ValueTask.CompletedTask;

    public ValueTask DeferAsync(Envelope envelope) => ValueTask.CompletedTask;

    public ValueTask StopAsync() => ValueTask.CompletedTask;

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;
}

public record RetryOnceMessage5547(string Reason);

public record RetryWithCooldownMessage5547(string Reason);

public record DoThenRetryMessage5547(string Reason);

public record ParentlessMessage5547(string Reason);

public record InlineListenerMessage5547(string Reason);

public class RetryOnce5547(string message) : Exception(message);

public class RetryWithCooldown5547(string message) : Exception(message);

public class DoThenRetry5547(string message) : Exception(message);

public static class InlineRetry5547Handler
{
    public static void Handle(RetryOnceMessage5547 message) => throw new RetryOnce5547(message.Reason);

    public static void Handle(RetryWithCooldownMessage5547 message) => throw new RetryWithCooldown5547(message.Reason);

    public static void Handle(DoThenRetryMessage5547 message) => throw new DoThenRetry5547(message.Reason);

    public static void Handle(ParentlessMessage5547 message) => throw new RetryOnce5547(message.Reason);

    public static void Handle(InlineListenerMessage5547 message) => throw new RetryOnce5547(message.Reason);
}
