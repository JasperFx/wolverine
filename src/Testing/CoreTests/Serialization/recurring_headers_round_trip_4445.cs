using System.Diagnostics;
using JasperFx.Core;
using Wolverine.Runtime;
using Wolverine.Runtime.Serialization;
using Xunit;

namespace CoreTests.Serialization;

/// <summary>
///     GH-4445. The schedule name and the occurrence instant ride an occurrence as ordinary loose
///     headers, and that is the whole reason the attribution works across a node boundary: the agent
///     that publishes an occurrence is usually not the node that handles it, and neither the metric
///     tag list (a private field, never serialized) nor <see cref="Envelope.ScheduledTime" /> (cleared
///     at fire time) survives that hop. These pin the wire contract — promoting either key into
///     <c>EnvelopeSerializer.ReservedHeaderKeys</c> would silently strip it from the loose headers and
///     take the trace tag and the per-schedule metric slice with it.
/// </summary>
public class recurring_headers_round_trip_4445
{
    private const string TheScheduleName = "nightly-rollup";
    private static readonly DateTimeOffset TheOccurrence = new(2026, 9, 15, 9, 0, 0, TimeSpan.Zero);

    private static Envelope theOccurrenceEnvelope()
    {
        var envelope = new Envelope
        {
            SentAt = DateTime.Today.ToUniversalTime(),
            Data = [1, 2, 3],
            Destination = "tcp://localhost:2222/incoming".ToUri(),
            MessageType = "occurrence-message-type",
            Id = Guid.NewGuid()
        };

        envelope.Headers[RecurringMessage.HeaderKey] = TheScheduleName;
        envelope.Headers[RecurringMessage.OccurrenceHeaderKey] = TheOccurrence.ToString("O");

        return envelope;
    }

    private static Envelope roundTrip(Envelope outgoing)
    {
        return EnvelopeSerializer.Deserialize(EnvelopeSerializer.Serialize(outgoing));
    }

    [Fact]
    public void both_recurring_headers_survive_the_wire()
    {
        var incoming = roundTrip(theOccurrenceEnvelope());

        incoming.Headers[RecurringMessage.HeaderKey].ShouldBe(TheScheduleName);
        incoming.Headers[RecurringMessage.OccurrenceHeaderKey].ShouldBe(TheOccurrence.ToString("O"));
    }

    [Fact]
    public void the_occurrence_header_parses_back_to_the_same_instant()
    {
        // Round-trippable ("O") and UTC on purpose: GH-3645 is the standing reminder that a
        // timestamp header written in a format nothing parses back is the same as not having it.
        var incoming = roundTrip(theOccurrenceEnvelope());

        incoming.TryGetHeader(RecurringMessage.OccurrenceHeaderKey, out var raw).ShouldBeTrue();

        var parsed = DateTimeOffset.Parse(raw!);
        parsed.ShouldBe(TheOccurrence);
        parsed.Offset.ShouldBe(TimeSpan.Zero);
    }

    [Fact]
    public void attribution_still_works_on_the_receiving_node()
    {
        // A handling node that never saw the publishing agent still tags its span and its counters,
        // purely off what came over the wire. This is the claim the feature actually rests on.
        var incoming = roundTrip(theOccurrenceEnvelope());

        var activity = new Activity("process");
        incoming.WriteTags(activity);

        activity.GetTagItem(WolverineTracing.ScheduleName).ShouldBe(TheScheduleName);
        activity.GetTagItem(WolverineTracing.ScheduleOccurrence).ShouldBe(TheOccurrence.ToString("O"));

        var tags = new Dictionary<string, object>(incoming.ToMetricsHeaders());
        tags[MetricsConstants.ScheduleNameKey].ShouldBe(TheScheduleName);
    }
}
