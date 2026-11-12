using System.Diagnostics;
using JasperFx.Core;
using Microsoft.Extensions.Hosting;
using Wolverine.ComplianceTests;
using Wolverine.Runtime;
using Wolverine.Tracking;
using Wolverine.Util;
using Xunit;

namespace CoreTests.Runtime;

public class new_linked_trace_per_message : IDisposable
{
    private const string SenderId = "00-25d8f5709b569a1f61bcaf79b9450ed4-f293c0545fc237a1-01";

    private readonly ActivityListener _listener = new()
    {
        ShouldListenTo = source => source.Name == "Wolverine",
        Sample = (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.AllDataAndRecorded
    };

    public new_linked_trace_per_message()
    {
        ActivitySource.AddActivityListener(_listener);
    }

    public void Dispose()
    {
        _listener.Dispose();
    }

    [Fact]
    public void receive_span_starts_a_new_trace_linked_to_the_sender()
    {
        var envelope = ObjectMother.Envelope();
        envelope.ParentId = SenderId;
        envelope.StartsNewLinkedTrace = true;

        using var receive = WolverineTracing.StartReceiving(envelope);

        receive.ShouldNotBeNull();
        receive.ParentId.ShouldBeNull();
        receive.TraceId.ToString().ShouldNotBe("25d8f5709b569a1f61bcaf79b9450ed4");

        var link = receive.Links.ShouldHaveSingleItem();
        link.Context.TraceId.ToString().ShouldBe("25d8f5709b569a1f61bcaf79b9450ed4");
        link.Context.SpanId.ToString().ShouldBe("f293c0545fc237a1");

        envelope.StartsNewLinkedTrace.ShouldBeFalse();
        envelope.TryGetHeader(EnvelopeConstants.StartsNewLinkedTraceKey, out _).ShouldBeFalse();
    }

    [Fact]
    public void process_span_after_the_receive_span_joins_the_new_trace()
    {
        var envelope = ObjectMother.Envelope();
        envelope.ParentId = SenderId;
        envelope.StartsNewLinkedTrace = true;

        using var receive = WolverineTracing.StartReceiving(envelope);
        using var process = WolverineTracing.StartExecuting(envelope);

        process.ShouldNotBeNull();
        process.TraceId.ShouldBe(receive!.TraceId);
        process.ParentSpanId.ShouldBe(receive.SpanId);
        process.Links.ShouldBeEmpty();
    }

    [Fact]
    public void process_span_starts_the_new_trace_when_there_is_no_receive_span()
    {
        var envelope = ObjectMother.Envelope();
        envelope.ParentId = SenderId;
        envelope.StartsNewLinkedTrace = true;

        using var process = WolverineTracing.StartExecuting(envelope);

        process.ShouldNotBeNull();
        process.ParentId.ShouldBeNull();
        process.Links.ShouldHaveSingleItem().Context.SpanId.ToString().ShouldBe("f293c0545fc237a1");
    }

    [Fact]
    public void an_ambient_activity_does_not_become_the_parent()
    {
        var envelope = ObjectMother.Envelope();
        envelope.ParentId = SenderId;
        envelope.StartsNewLinkedTrace = true;

        using var ambient = new Activity("listener").Start();

        using var receive = WolverineTracing.StartReceiving(envelope);

        receive.ShouldNotBeNull();
        receive.ParentId.ShouldBeNull();
        receive.TraceId.ShouldNotBe(ambient.TraceId);
    }

    [Fact]
    public void an_unmarked_envelope_still_continues_the_senders_trace()
    {
        var envelope = ObjectMother.Envelope();
        envelope.ParentId = SenderId;

        using var receive = WolverineTracing.StartReceiving(envelope);

        receive.ShouldNotBeNull();
        receive.ParentId.ShouldBe(SenderId);
        receive.Links.ShouldBeEmpty();
        envelope.ParentId.ShouldBe(SenderId);
    }

    [Fact]
    public void the_send_span_stays_in_the_senders_trace()
    {
        var envelope = ObjectMother.Envelope();
        envelope.ParentId = SenderId;
        envelope.StartsNewLinkedTrace = true;

        using var send = WolverineTracing.StartSending(envelope);

        send.ShouldNotBeNull();
        send.ParentId.ShouldBe(SenderId);
        envelope.StartsNewLinkedTrace.ShouldBeTrue();
    }

    [Fact]
    public void delivery_options_mark_the_envelope()
    {
        var envelope = ObjectMother.Envelope();

        new DeliveryOptions { StartsNewLinkedTrace = true }.Override(envelope);

        envelope.StartsNewLinkedTrace.ShouldBeTrue();
    }
}

public class new_linked_trace_per_message_end_to_end : IAsyncLifetime
{
    private readonly List<Activity> _stopped = new();
    private ActivityListener _listener = null!;
    private IHost _host = null!;

    public async ValueTask InitializeAsync()
    {
        _listener = new ActivityListener
        {
            ShouldListenTo = source => source.Name == "Wolverine",
            Sample = (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.AllDataAndRecorded,
            ActivityStopped = activity =>
            {
                lock (_stopped)
                {
                    _stopped.Add(activity);
                }
            }
        };
        ActivitySource.AddActivityListener(_listener);

        _host = await Host.CreateDefaultBuilder()
            .UseWolverine(opts =>
            {
                opts.Discovery.DisableConventionalDiscovery().IncludeType<FanOutHandler>();
                opts.MetadataRules.Add(new MarkLinkedTraceByRule());
            }).StartAsync();
    }

    public async ValueTask DisposeAsync()
    {
        _listener.Dispose();
        await _host.StopAsync();
        _host.Dispose();
    }

    [Fact]
    public async Task marked_messages_get_their_own_trace_linked_to_the_handler_that_sent_them()
    {
        var trigger = new FanOutTrigger(Guid.NewGuid());

        var session = await _host.TrackActivity().IncludeExternalTransports().InvokeMessageAndWaitAsync(trigger);

        var itemIds = session.Executed.MessagesOf<FannedOutItem>().ToArray();
        itemIds.Length.ShouldBe(3);

        await Task.Delay(100.Milliseconds(), TestContext.Current.CancellationToken);

        Activity[] spans;
        lock (_stopped)
        {
            spans = _stopped.ToArray();
        }

        var triggerEnvelope = session.Executed.Envelopes().Single(e => e.Message is FanOutTrigger);
        var handler = spans.Single(x =>
            x.OperationName == typeof(FanOutTrigger).ToMessageTypeName() &&
            Equals(x.GetTagItem(WolverineTracing.MessagingMessageId), triggerEnvelope.Id));

        foreach (var item in itemIds)
        {
            var envelope = session.Executed.Envelopes().Single(e => Equals(e.Message, item));
            var itemSpans = spans
                .Where(x => Equals(x.GetTagItem(WolverineTracing.MessagingMessageId), envelope.Id))
                .Where(x => x.Kind != ActivityKind.Producer)
                .OrderBy(x => x.StartTimeUtc)
                .ToArray();

            itemSpans.ShouldNotBeEmpty();

            if (item.Marked)
            {
                var root = itemSpans[0];
                root.TraceId.ShouldNotBe(handler.TraceId);
                root.ParentId.ShouldBeNull();

                var link = root.Links.ShouldHaveSingleItem();
                link.Context.TraceId.ShouldBe(handler.TraceId);
                link.Context.SpanId.ShouldBe(handler.SpanId);

                itemSpans.Skip(1).ShouldAllBe(x => x.TraceId == root.TraceId && !x.Links.Any());
            }
            else
            {
                itemSpans.ShouldAllBe(x => x.TraceId == handler.TraceId && !x.Links.Any());
            }

            envelope.CorrelationId.ShouldBe(triggerEnvelope.CorrelationId);
        }
    }
}

public record FanOutTrigger(Guid Id);

public record FannedOutItem(Guid Id, string How)
{
    public bool Marked => How != "unmarked";
}

public class MarkLinkedTraceByRule : IEnvelopeRule
{
    public void Modify(Envelope envelope)
    {
        if (envelope.Message is FannedOutItem { How: "rule" })
        {
            envelope.StartsNewLinkedTrace = true;
        }
    }
}

public class FanOutHandler
{
    public static async Task Handle(FanOutTrigger trigger, IMessageBus bus)
    {
        await bus.SendAsync(new FannedOutItem(Guid.NewGuid(), "delivery-options"),
            new DeliveryOptions { StartsNewLinkedTrace = true });
        await bus.SendAsync(new FannedOutItem(Guid.NewGuid(), "rule"));
        await bus.SendAsync(new FannedOutItem(Guid.NewGuid(), "unmarked"));
    }

    public static void Handle(FannedOutItem item)
    {
    }
}
