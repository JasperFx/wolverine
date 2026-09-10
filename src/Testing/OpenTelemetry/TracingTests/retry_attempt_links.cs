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

                // Inline (Rabbit MQ's default) hands the receive span to the pipeline as the span the handler
                // runs in; buffered starts a separate short receive span first and a process span later. Both
                // are covered because they reach the pipeline by different doors.
                opts.PublishMessage<RabbitInlineRequeue>().ToRabbitQueue(InlineQueue);
                opts.ListenToRabbitQueue(InlineQueue);

                opts.PublishMessage<RabbitBufferedRequeue>().ToRabbitQueue(BufferedQueue);
                opts.ListenToRabbitQueue(BufferedQueue).BufferedInMemory();

                opts.Services.AddOpenTelemetry()
                    .WithTracing(tracing => tracing.AddSource("Wolverine").AddInMemoryExporter(_spans));
            }).StartAsync();
    }

    public async ValueTask DisposeAsync()
    {
        await _host.StopAsync();
        _host.Dispose();
    }

    [Fact]
    public async Task an_attempt_that_succeeds_first_time_has_no_links()
    {
        var attempts = await attemptsFor(new LocalRetryNow(Guid.NewGuid(), 0), 1);

        attempts.Single().Links.ShouldBeEmpty();
    }

    [Fact]
    public async Task retry_now_links_each_attempt_to_the_one_before_it()
    {
        var message = new LocalRetryNow(Guid.NewGuid(), 2);

        var attempts = await attemptsFor(message, 3);

        shouldBeChained(attempts);
        RetryLinkHandler.SawPreviousAttemptHeader(message.Id).ShouldBeFalse();
    }

    [Fact]
    public async Task scheduled_retry_links_each_attempt_to_the_one_before_it()
    {
        var message = new LocalScheduledRetry(Guid.NewGuid(), 2);

        var attempts = await attemptsFor(message, 3);

        shouldBeChained(attempts);
        RetryLinkHandler.SawPreviousAttemptHeader(message.Id).ShouldBeFalse();
    }

    [Fact]
    public async Task requeue_links_each_attempt_to_the_one_before_it()
    {
        var message = new LocalRequeue(Guid.NewGuid(), 2);

        var attempts = await attemptsFor(message, 3);

        shouldBeChained(attempts);
        RetryLinkHandler.SawPreviousAttemptHeader(message.Id).ShouldBeFalse();
    }

    // A Rabbit MQ requeue acks the delivery and publishes a fresh copy, so the only way the next attempt can
    // know what failed before it is a header that survived the round trip through the broker

    [Fact]
    public async Task requeue_through_an_inline_rabbitmq_listener_carries_the_link_across_the_broker()
    {
        var message = new RabbitInlineRequeue(Guid.NewGuid(), 1);

        var attempts = await attemptsFor(message, 2, processedInsideReceiveSpan: true);

        shouldBeChained(attempts);
        RetryLinkHandler.SawPreviousAttemptHeader(message.Id).ShouldBeFalse();
    }

    [Fact]
    public async Task requeue_through_a_buffered_rabbitmq_listener_links_the_process_span_not_the_receive_span()
    {
        var message = new RabbitBufferedRequeue(Guid.NewGuid(), 1);

        var attempts = await attemptsFor(message, 2);

        shouldBeChained(attempts);
        RetryLinkHandler.SawPreviousAttemptHeader(message.Id).ShouldBeFalse();

        // The receive span starts before the pipeline does, so it must not be the span that takes the link
        var messageId = attempts[0].GetTagItem(WolverineTracing.MessagingMessageId)!.ToString();
        var receives = _spans.Snapshot()
            .Where(x => x.DisplayName == "receive" &&
                        x.GetTagItem(WolverineTracing.MessagingMessageId)?.ToString() == messageId)
            .ToArray();

        receives.Length.ShouldBe(2);
        receives.ShouldAllBe(x => !x.Links.Any());
    }

    private async Task<Activity[]> attemptsFor<T>(T message, int expectedAttempts,
        bool processedInsideReceiveSpan = false) where T : notnull
    {
        var session = await _host
            .TrackActivity()
            .DoNotAssertOnExceptionsDetected()
            .IncludeExternalTransports()
            .Timeout(30.Seconds())
            .SendMessageAndWaitAsync(message);

        var envelope = session.MessageSucceeded.SingleEnvelope<T>();
        var messageId = envelope.Id.ToString();
        var spanName = processedInsideReceiveSpan ? "receive" : envelope.MessageType;

        // The tracked session completes when the message succeeds, which can be a beat before the pipeline's
        // finally stops -- and therefore exports -- the final attempt's span
        var attempts = await _spans.WaitForAsync(
            x => x.DisplayName == spanName &&
                 x.GetTagItem(WolverineTracing.MessagingMessageId)?.ToString() == messageId,
            expectedAttempts);

        attempts.Length.ShouldBe(expectedAttempts);

        return attempts.OrderBy(x => x.StartTimeUtc).ToArray();
    }

    private static void shouldBeChained(Activity[] attempts)
    {
        attempts[0].Links.ShouldBeEmpty();

        for (var i = 1; i < attempts.Length; i++)
        {
            var link = attempts[i].Links.ShouldHaveSingleItem();
            link.Context.TraceId.ShouldBe(attempts[i - 1].TraceId);
            link.Context.SpanId.ShouldBe(attempts[i - 1].SpanId);
        }
    }
}

public record LocalRetryNow(Guid Id, int Failures);

public record LocalScheduledRetry(Guid Id, int Failures);

public record LocalRequeue(Guid Id, int Failures);

public record RabbitInlineRequeue(Guid Id, int Failures);

public record RabbitBufferedRequeue(Guid Id, int Failures);

public class RetryNowFailure() : Exception("Failing so the message is retried inline");

public class ScheduledRetryFailure() : Exception("Failing so the message is rescheduled");

public class RequeueFailure() : Exception("Failing so the message is requeued");

public class RetryLinkHandler
{
    private static readonly ConcurrentDictionary<Guid, int> _attempts = new();
    private static readonly ConcurrentDictionary<Guid, bool> _sawHeader = new();

    public static bool SawPreviousAttemptHeader(Guid id) => _sawHeader.ContainsKey(id);

    public void Handle(LocalRetryNow message, Envelope envelope) =>
        failUntil<RetryNowFailure>(message.Id, message.Failures, envelope);

    public void Handle(LocalScheduledRetry message, Envelope envelope) =>
        failUntil<ScheduledRetryFailure>(message.Id, message.Failures, envelope);

    public void Handle(LocalRequeue message, Envelope envelope) =>
        failUntil<RequeueFailure>(message.Id, message.Failures, envelope);

    public void Handle(RabbitInlineRequeue message, Envelope envelope) =>
        failUntil<RequeueFailure>(message.Id, message.Failures, envelope);

    public void Handle(RabbitBufferedRequeue message, Envelope envelope) =>
        failUntil<RequeueFailure>(message.Id, message.Failures, envelope);

    private static void failUntil<T>(Guid id, int failures, Envelope envelope) where T : Exception, new()
    {
        // The pipeline consumes the header before the handler runs, so a handler -- and anything it
        // cascades or propagates -- never sees it
        if (envelope.TryGetHeader(EnvelopeConstants.PreviousAttemptActivityIdKey, out _))
        {
            _sawHeader[id] = true;
        }

        if (_attempts.AddOrUpdate(id, 1, (_, count) => count + 1) <= failures)
        {
            throw new T();
        }
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
