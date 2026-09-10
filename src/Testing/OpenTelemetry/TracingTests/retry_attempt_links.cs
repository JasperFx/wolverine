using System.Collections;
using System.Collections.Concurrent;
using System.Diagnostics;
using JasperFx.Core;
using OpenTelemetry.Trace;
using Shouldly;
using Wolverine;
using Wolverine.ErrorHandling;
using Wolverine.RabbitMQ;
using Wolverine.Runtime;
using Wolverine.Tracking;
using Xunit;

namespace TracingTests;

/// <summary>
/// GH-4398. End to end through the real OpenTelemetry SDK: every Wolverine span this host produces goes
/// through the SDK's sampler and export pipeline into an in-memory exporter, so these tests assert on what
/// a trace backend would actually have received rather than on Activity objects poked at in isolation.
///
/// Every failure policy that gives a message another attempt runs against both shapes of listener, because
/// they reach the handler pipeline by different doors. Inline (Rabbit MQ's default) hands the receive span
/// to the pipeline as the span the handler runs in. Buffered starts a short receive span first and a separate
/// process span later -- Durable does exactly the same, so Buffered stands in for both. Local queues cannot
/// run Inline at all, so the Inline half of the matrix is Rabbit MQ's.
///
/// Which span an attempt "is" differs by path -- a retry-now on an Inline listener re-runs the pipeline in a
/// new process span, and with no message store a scheduled retry comes back through the local replies queue
/// -- so rather than guessing from span names, the handler records the span it actually ran in.
/// </summary>
[Collection("otel")]
public class retry_attempt_links : IAsyncLifetime
{
    private const string InlineQueue = "retry-attempt-links-inline";
    private const string BufferedQueue = "retry-attempt-links-buffered";

    private readonly ExportedSpans _spans = new();
    private IHost _host = null!;

    public async ValueTask InitializeAsync()
    {
        _host = await Host.CreateDefaultBuilder()
            .UseWolverine(opts =>
            {
                opts.ServiceName = "RetryAttemptLinks";
                opts.ApplicationAssembly = GetType().Assembly;

                opts.Discovery.DisableConventionalDiscovery().IncludeType<RetryLinkHandler>();

                opts.OnException<RetryNowFailure>().RetryTimes(3);
                opts.OnException<ScheduledRetryFailure>()
                    .ScheduleRetry(50.Milliseconds(), 50.Milliseconds(), 50.Milliseconds());
                opts.OnException<RequeueFailure>().Requeue(5);

                opts.UseRabbitMq().AutoProvision().AutoPurgeOnStartup();

                opts.PublishMessage<RabbitInlineAttempt>().ToRabbitQueue(InlineQueue);
                opts.ListenToRabbitQueue(InlineQueue).ProcessInline();

                opts.PublishMessage<RabbitBufferedAttempt>().ToRabbitQueue(BufferedQueue);
                opts.ListenToRabbitQueue(BufferedQueue).BufferedInMemory();

                // LocalBufferedAttempt stays on its conventional local queue, which is buffered

                opts.Services.AddOpenTelemetry()
                    .WithTracing(tracing => tracing.AddSource("Wolverine").AddInMemoryExporter(_spans));
            }).StartAsync();
    }

    public async ValueTask DisposeAsync()
    {
        await _host.StopAsync();
        _host.Dispose();
    }

    public static TheoryData<ListenerKind, FailureKind> EveryListenerAndFailurePolicy()
    {
        var data = new TheoryData<ListenerKind, FailureKind>();
        foreach (var listener in Enum.GetValues<ListenerKind>())
        foreach (var failure in Enum.GetValues<FailureKind>())
        {
            data.Add(listener, failure);
        }

        return data;
    }

    [Theory]
    [MemberData(nameof(EveryListenerAndFailurePolicy))]
    public async Task each_attempt_links_to_the_one_before_it(ListenerKind listener, FailureKind failure)
    {
        var id = Guid.NewGuid();

        var (attempts, everyOtherSpan) = await attemptsFor(listener, new Attempt(id, failure, 2), 3);

        attempts[0].Links.ShouldBeEmpty();
        for (var i = 1; i < attempts.Length; i++)
        {
            var link = attempts[i].Links.ShouldHaveSingleItem();
            link.Context.TraceId.ShouldBe(attempts[i - 1].TraceId);
            link.Context.SpanId.ShouldBe(attempts[i - 1].SpanId);
        }

        // The pipeline consumes the header before the handler runs, so a handler -- and anything it cascades
        // or propagates -- never sees it
        RetryLinkHandler.SawPreviousAttemptHeader(id).ShouldBeFalse();

        // Only the spans the handler ran in may carry the link. A buffered listener's receive span starts
        // before the pipeline does, which is exactly the span that must not take it.
        if (listener == ListenerKind.RabbitBuffered) everyOtherSpan.ShouldNotBeEmpty();
        everyOtherSpan.ShouldAllBe(x => !x.Links.Any());
    }

    [Theory]
    [InlineData(ListenerKind.LocalBuffered)]
    [InlineData(ListenerKind.RabbitInline)]
    [InlineData(ListenerKind.RabbitBuffered)]
    public async Task an_attempt_that_succeeds_first_time_has_no_links(ListenerKind listener)
    {
        var (attempts, _) = await attemptsFor(listener, new Attempt(Guid.NewGuid(), FailureKind.RetryNow, 0), 1);

        attempts.Single().Links.ShouldBeEmpty();
    }

    /// <summary>
    /// Returns the exported span each attempt's handler ran in, in attempt order, and every other exported
    /// span for the same message.
    /// </summary>
    private async Task<(Activity[] attempts, Activity[] everyOtherSpan)> attemptsFor(ListenerKind listener,
        Attempt attempt, int expectedAttempts)
    {
        object message = listener switch
        {
            ListenerKind.LocalBuffered => new LocalBufferedAttempt(attempt),
            ListenerKind.RabbitInline => new RabbitInlineAttempt(attempt),
            ListenerKind.RabbitBuffered => new RabbitBufferedAttempt(attempt),
            _ => throw new ArgumentOutOfRangeException(nameof(listener))
        };

        var session = await _host
            .TrackActivity()
            .DoNotAssertOnExceptionsDetected()
            .IncludeExternalTransports()
            .Timeout(30.Seconds())
            .SendMessageAndWaitAsync(message);

        session.MessageSucceeded.Envelopes().ShouldContain(x => x.Message!.GetType() == message.GetType());

        var handlerSpans = RetryLinkHandler.SpansFor(attempt.Id);
        handlerSpans.Length.ShouldBe(expectedAttempts);

        // The tracked session completes when the message succeeds, which can be a beat before the pipeline's
        // finally stops -- and therefore exports -- the final attempt's span
        var exported = await _spans.WaitForAsync(x => handlerSpans.Contains(x.SpanId), expectedAttempts);
        exported.Length.ShouldBe(expectedAttempts);

        var attempts = handlerSpans.Select(spanId => exported.Single(x => x.SpanId == spanId)).ToArray();

        var messageId = attempts[0].GetTagItem(WolverineTracing.MessagingMessageId)!.ToString();
        var everyOtherSpan = _spans.Snapshot()
            .Where(x => !handlerSpans.Contains(x.SpanId) &&
                        x.GetTagItem(WolverineTracing.MessagingMessageId)?.ToString() == messageId)
            .ToArray();

        return (attempts, everyOtherSpan);
    }
}

public enum ListenerKind
{
    LocalBuffered,
    RabbitInline,
    RabbitBuffered
}

public enum FailureKind
{
    RetryNow,
    ScheduledRetry,
    Requeue
}

public record Attempt(Guid Id, FailureKind Failure, int Failures);

public record LocalBufferedAttempt(Attempt Attempt);

public record RabbitInlineAttempt(Attempt Attempt);

public record RabbitBufferedAttempt(Attempt Attempt);

public class RetryNowFailure() : Exception("Failing so the message is retried inline");

public class ScheduledRetryFailure() : Exception("Failing so the message is rescheduled");

public class RequeueFailure() : Exception("Failing so the message is requeued");

public class RetryLinkHandler
{
    private static readonly ConcurrentDictionary<Guid, ConcurrentQueue<ActivitySpanId>> _spans = new();
    private static readonly ConcurrentDictionary<Guid, bool> _sawHeader = new();

    public static bool SawPreviousAttemptHeader(Guid id) => _sawHeader.ContainsKey(id);

    public static ActivitySpanId[] SpansFor(Guid id) =>
        _spans.TryGetValue(id, out var spans) ? spans.ToArray() : [];

    public void Handle(LocalBufferedAttempt message, Envelope envelope) => failUntil(message.Attempt, envelope);

    public void Handle(RabbitInlineAttempt message, Envelope envelope) => failUntil(message.Attempt, envelope);

    public void Handle(RabbitBufferedAttempt message, Envelope envelope) => failUntil(message.Attempt, envelope);

    private static void failUntil(Attempt attempt, Envelope envelope)
    {
        if (envelope.TryGetHeader(EnvelopeConstants.PreviousAttemptActivityIdKey, out _))
        {
            _sawHeader[attempt.Id] = true;
        }

        var spans = _spans.GetOrAdd(attempt.Id, _ => new ConcurrentQueue<ActivitySpanId>());
        spans.Enqueue(Activity.Current!.SpanId);

        if (spans.Count > attempt.Failures)
        {
            return;
        }

        throw attempt.Failure switch
        {
            FailureKind.RetryNow => new RetryNowFailure(),
            FailureKind.ScheduledRetry => new ScheduledRetryFailure(),
            FailureKind.Requeue => new RequeueFailure(),
            _ => new ArgumentOutOfRangeException(nameof(attempt))
        };
    }
}

/// <summary>
/// The in-memory exporter appends from whichever thread stopped the span; this is only a lock around a list
/// so a test can read it while the host is still exporting.
/// </summary>
internal class ExportedSpans : ICollection<Activity>
{
    private readonly List<Activity> _inner = new();
    private readonly object _lock = new();

    public Activity[] Snapshot()
    {
        lock (_lock) return _inner.ToArray();
    }

    public async Task<Activity[]> WaitForAsync(Func<Activity, bool> filter, int count)
    {
        var deadline = DateTime.UtcNow.AddSeconds(10);
        while (true)
        {
            var matches = Snapshot().Where(filter).ToArray();
            if (matches.Length >= count || DateTime.UtcNow > deadline) return matches;

            await Task.Delay(25);
        }
    }

    public void Add(Activity item)
    {
        lock (_lock) _inner.Add(item);
    }

    public void Clear()
    {
        lock (_lock) _inner.Clear();
    }

    public bool Contains(Activity item)
    {
        lock (_lock) return _inner.Contains(item);
    }

    public void CopyTo(Activity[] array, int arrayIndex)
    {
        lock (_lock) _inner.CopyTo(array, arrayIndex);
    }

    public bool Remove(Activity item)
    {
        lock (_lock) return _inner.Remove(item);
    }

    public int Count
    {
        get
        {
            lock (_lock) return _inner.Count;
        }
    }

    public bool IsReadOnly => false;

    public IEnumerator<Activity> GetEnumerator() => ((IEnumerable<Activity>)Snapshot()).GetEnumerator();

    IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
}
