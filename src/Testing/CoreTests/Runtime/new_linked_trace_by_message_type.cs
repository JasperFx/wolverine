using System.Diagnostics;
using JasperFx.Core;
using Microsoft.Extensions.Hosting;
using Wolverine.Attributes;
using Wolverine.ComplianceTests;
using Wolverine.Runtime;
using Wolverine.Runtime.Routing;
using Wolverine.Tracking;
using Wolverine.Util;
using Xunit;

namespace CoreTests.Runtime;

// GH-5537: choose once, by message type, which messages start a new trace linked to their sender
public class new_linked_trace_by_message_type_rules
{
    [Fact]
    public void attribute_is_in_the_per_message_type_rules()
    {
        MessageRoute.RulesForMessageType(typeof(LinkedByAttribute))
            .OfType<StartsNewLinkedTraceAttribute>().ShouldHaveSingleItem();
    }

    [Fact]
    public void marker_interface_is_in_the_per_message_type_rules()
    {
        MessageRoute.RulesForMessageType(typeof(LinkedByMarker))
            .OfType<StartsNewLinkedTraceRule>().ShouldHaveSingleItem();
    }

    [Fact]
    public void attribute_and_marker_together_mark_the_envelope_once()
    {
        MessageRoute.RulesForMessageType(typeof(LinkedByAttributeAndMarker)).Count().ShouldBe(1);
    }

    [Fact]
    public void an_unmarked_type_has_no_linked_trace_rule()
    {
        MessageRoute.RulesForMessageType(typeof(NotLinked)).ShouldBeEmpty();
    }

    [Fact]
    public void tracing_options_resolve_rules_by_type_and_predicate()
    {
        var options = new TracingOptions()
            .StartNewLinkedTraceFor<LinkedByRule>()
            .StartNewLinkedTraceFor(type => type.Name.StartsWith("LinkedByPredicate"));

        options.RulesFor(typeof(LinkedByRule)).ShouldHaveSingleItem();
        options.RulesFor(typeof(LinkedByPredicate)).ShouldHaveSingleItem();
        options.RulesFor(typeof(NotLinked)).ShouldBeEmpty();
    }

    [Fact]
    public void two_matching_rules_still_yield_one_envelope_rule()
    {
        var options = new TracingOptions()
            .StartNewLinkedTraceFor<LinkedByRule>()
            .StartNewLinkedTraceFor(_ => true);

        options.RulesFor(typeof(LinkedByRule)).ShouldHaveSingleItem();
    }

    [Fact]
    public void the_rule_marks_the_envelope()
    {
        var envelope = ObjectMother.Envelope();
        StartsNewLinkedTraceRule.Instance.Modify(envelope);
        envelope.StartsNewLinkedTrace.ShouldBeTrue();
    }

    [Fact]
    public void delivery_options_left_null_do_not_touch_the_envelope()
    {
        var envelope = ObjectMother.Envelope();
        envelope.StartsNewLinkedTrace = true;

        new DeliveryOptions().Override(envelope);

        envelope.StartsNewLinkedTrace.ShouldBeTrue();
    }

    [Fact]
    public void explicit_false_in_delivery_options_clears_a_rule()
    {
        var envelope = ObjectMother.Envelope();
        envelope.StartsNewLinkedTrace = true;

        new DeliveryOptions { StartsNewLinkedTrace = false }.Override(envelope);

        envelope.StartsNewLinkedTrace.ShouldBeFalse();
    }
}

public class new_linked_trace_by_message_type_end_to_end : IAsyncLifetime
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

        #region sample_start_new_linked_trace_for_message_types
        _host = await Host.CreateDefaultBuilder()
            .UseWolverine(opts =>
            {
                opts.Discovery.DisableConventionalDiscovery().IncludeType<LinkedTraceTriggerHandler>();

                // Every message that can be cast to ILinkedTraceEvent starts a new trace
                // linked to the activity that sent it
                opts.Tracing.StartNewLinkedTraceFor<ILinkedTraceEvent>();

                // Or choose the message types with a predicate
                opts.Tracing.StartNewLinkedTraceFor(type => type.Name.StartsWith("LinkedByPredicate"));
            }).StartAsync();
        #endregion
    }

    public async ValueTask DisposeAsync()
    {
        _listener.Dispose();
        await _host.StopAsync();
        _host.Dispose();
    }

    [Fact]
    public async Task each_rule_starts_a_new_trace_linked_to_the_sender_and_unmarked_types_stay_in_it()
    {
        var trigger = new LinkedTraceTrigger(Guid.NewGuid());

        var session = await _host.TrackActivity().InvokeMessageAndWaitAsync(trigger);

        await Task.Delay(100.Milliseconds(), TestContext.Current.CancellationToken);

        Activity[] spans;
        lock (_stopped)
        {
            spans = _stopped.ToArray();
        }

        var triggerEnvelope = session.Executed.Envelopes().Single(e => e.Message is LinkedTraceTrigger);
        var handler = spans.Single(x =>
            x.OperationName == typeof(LinkedTraceTrigger).ToMessageTypeName() &&
            Equals(x.GetTagItem(WolverineTracing.MessagingMessageId), triggerEnvelope.Id));

        void assertLinked(Envelope envelope)
        {
            var itemSpans = spansFor(spans, envelope);
            var root = itemSpans[0];
            root.TraceId.ShouldNotBe(handler.TraceId);
            root.ParentId.ShouldBeNull();

            var link = root.Links.ShouldHaveSingleItem();
            link.Context.TraceId.ShouldBe(handler.TraceId);
            link.Context.SpanId.ShouldBe(handler.SpanId);

            itemSpans.Skip(1).ShouldAllBe(x => x.TraceId == root.TraceId && !x.Links.Any());
        }

        void assertInSenderTrace(Envelope envelope)
        {
            spansFor(spans, envelope).ShouldAllBe(x => x.TraceId == handler.TraceId && !x.Links.Any());
        }

        Envelope sent<T>(Func<T, bool>? filter = null)
        {
            return session.Executed.Envelopes()
                .Single(e => e.Message is T m && (filter == null || filter(m)));
        }

        // opts.Tracing.StartNewLinkedTraceFor<T>()
        assertLinked(sent<LinkedByRule>());

        // opts.Tracing.StartNewLinkedTraceFor(predicate)
        assertLinked(sent<LinkedByPredicate>());

        // [StartsNewLinkedTrace]
        assertLinked(sent<LinkedByAttribute>(x => !x.OptedOut));

        // IStartsNewLinkedTrace
        assertLinked(sent<LinkedByMarker>());

        // no rule at all
        assertInSenderTrace(sent<NotLinked>());

        // an explicit DeliveryOptions.StartsNewLinkedTrace = false wins over the attribute
        assertInSenderTrace(sent<LinkedByAttribute>(x => x.OptedOut));

        // a cascaded message returned from the handler follows the rule too
        assertLinked(sent<CascadedLinkedByRule>());
    }

    private static Activity[] spansFor(Activity[] spans, Envelope envelope)
    {
        var itemSpans = spans
            .Where(x => Equals(x.GetTagItem(WolverineTracing.MessagingMessageId), envelope.Id))
            .Where(x => x.Kind != ActivityKind.Producer)
            .OrderBy(x => x.StartTimeUtc)
            .ToArray();

        itemSpans.ShouldNotBeEmpty();
        return itemSpans;
    }
}

public record LinkedTraceTrigger(Guid Id);

public interface ILinkedTraceEvent;

public record LinkedByRule(Guid Id) : ILinkedTraceEvent;

public record CascadedLinkedByRule(Guid Id) : ILinkedTraceEvent;

public record LinkedByPredicate(Guid Id);

#region sample_starts_new_linked_trace_attribute
[StartsNewLinkedTrace]
public record LinkedByAttribute(Guid Id, bool OptedOut = false);
#endregion

#region sample_starts_new_linked_trace_marker_interface
public record LinkedByMarker(Guid Id) : IStartsNewLinkedTrace;
#endregion

[StartsNewLinkedTrace]
public record LinkedByAttributeAndMarker(Guid Id) : IStartsNewLinkedTrace;

public record NotLinked(Guid Id);

public class LinkedTraceTriggerHandler
{
    public static async Task<CascadedLinkedByRule> Handle(LinkedTraceTrigger trigger, IMessageBus bus)
    {
        await bus.SendAsync(new LinkedByRule(Guid.NewGuid()));
        await bus.SendAsync(new LinkedByPredicate(Guid.NewGuid()));
        await bus.SendAsync(new LinkedByAttribute(Guid.NewGuid()));
        await bus.SendAsync(new LinkedByMarker(Guid.NewGuid()));
        await bus.SendAsync(new NotLinked(Guid.NewGuid()));

        #region sample_opt_out_of_linked_trace_rule
        // An explicit DeliveryOptions setting wins over the attribute, the marker and opts.Tracing
        await bus.SendAsync(new LinkedByAttribute(Guid.NewGuid(), OptedOut: true),
            new DeliveryOptions { StartsNewLinkedTrace = false });
        #endregion

        return new CascadedLinkedByRule(Guid.NewGuid());
    }

    public static void Handle(LinkedByRule message)
    {
    }

    public static void Handle(CascadedLinkedByRule message)
    {
    }

    public static void Handle(LinkedByPredicate message)
    {
    }

    public static void Handle(LinkedByAttribute message)
    {
    }

    public static void Handle(LinkedByMarker message)
    {
    }

    public static void Handle(NotLinked message)
    {
    }
}
