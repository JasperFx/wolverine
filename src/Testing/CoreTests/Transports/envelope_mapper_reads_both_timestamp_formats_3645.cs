using Shouldly;
using Wolverine.Runtime.Serialization;
using Wolverine.Transports;
using Xunit;

namespace CoreTests.Transports;

/// <summary>
///     GH-3645, backported from 6.x (#3656). Wolverine has two writers for the same timestamp header keys:
///     <see cref="EnvelopeMapper{TIncoming,TOutgoing}" /> emits its own
///     <c>"yyyy-MM-dd HH:mm:ss:ffffff Z"</c>, while <see cref="EnvelopeSerializer" /> emits round-trippable
///     <c>"o"</c>. The mapper's reader only ever accepted its own format, so an <c>"o"</c> value -- from the
///     serializer, from a non-Wolverine producer, or from a 6.x host that has flipped its writers -- bound as
///     <c>default</c> / <c>null</c> silently. That silent null is precisely the failure mode of GH-1716 and
///     GH-3613: the message arrives, the schedule is simply gone.
///
///     <para>Why this is on the 5.x line at all: 6.x is going to flip its writers to <c>"o"</c>, the one format a
///     stock date parser can read back. Every reader has to tolerate both in a <b>shipped</b> release before that
///     can happen, or a 6.x sender meeting a 5.x receiver during a migration reintroduces the silent null. The
///     5.x <b>writing</b> side is deliberately untouched and pinned below -- a 5.x host keeps emitting the legacy
///     format, so this is purely additive tolerance for a format it will now meet.</para>
/// </summary>
public class envelope_mapper_reads_both_timestamp_formats_3645
{
    private const string LegacyFormat = "yyyy-MM-dd HH:mm:ss:ffffff Z";

    private static readonly DateTimeOffset TheTimestamp =
        new DateTimeOffset(2026, 7, 30, 18, 25, 40, TimeSpan.Zero).AddTicks(4828020);

    private static Envelope readBack(string headerValue, string key)
    {
        var incoming = new StubTransportMessage();
        incoming.Headers[key] = headerValue;

        var envelope = new Envelope();
        new StubEnvelopeMapper(new StubEndpoint()).MapIncomingToEnvelope(envelope, incoming);
        return envelope;
    }

    [Fact]
    public void reads_the_legacy_transport_header_format()
    {
        // Unchanged behaviour: this is still what a 5.x mapper writes.
        readBack(TheTimestamp.ToString(LegacyFormat), EnvelopeConstants.ExecutionTimeKey)
            .ScheduledTime!.Value.ToUniversalTime().ShouldBe(TheTimestamp);
    }

    [Fact]
    public void reads_the_round_trippable_format()
    {
        // The backport. An "o" value used to bind null -- this is what a 6.x sender will put on the wire.
        readBack(TheTimestamp.ToString("o"), EnvelopeConstants.ExecutionTimeKey)
            .ScheduledTime!.Value.ToUniversalTime().ShouldBe(TheTimestamp);
    }

    [Fact]
    public void reads_a_deliver_by_in_the_round_trippable_format()
    {
        readBack(TheTimestamp.ToString("o"), EnvelopeConstants.DeliverByKey)
            .DeliverBy!.Value.ToUniversalTime().ShouldBe(TheTimestamp);
    }

    [Fact]
    public void an_unreadable_timestamp_still_binds_nothing_rather_than_throwing()
    {
        readBack("not a timestamp at all", EnvelopeConstants.ExecutionTimeKey)
            .ScheduledTime.ShouldBeNull();
    }

    /// <summary>
    ///     The point of the whole exercise: a value written by Wolverine's OTHER writer must be readable by the
    ///     mapper. Before this change the two sides could not read each other.
    /// </summary>
    [Fact]
    public void a_value_written_by_the_envelope_serializer_is_readable_by_the_mapper()
    {
        var outgoing = new Envelope
        {
            ScheduledTime = TheTimestamp,
            MessageType = "some-message",
            Data = [1, 2, 3]
        };

        var roundTripped = EnvelopeSerializer.Deserialize(EnvelopeSerializer.Serialize(outgoing));

        readBack(roundTripped.ScheduledTime!.Value.ToString("o"), EnvelopeConstants.ExecutionTimeKey)
            .ScheduledTime!.Value.ToUniversalTime().ShouldBe(TheTimestamp);
    }

    /// <summary>
    ///     Pins the writer as unchanged. 5.x is the OLD side of the migration this exists for -- it must keep
    ///     emitting what 5.x receivers already understand. Flipping writers is a 6.x change, and flipping them
    ///     here would create the very mixed-version break the backport is meant to prevent.
    /// </summary>
    [Fact]
    public void the_writer_still_emits_the_legacy_format()
    {
        var outgoing = new Envelope { ScheduledTime = TheTimestamp };
        var written = new StubTransportMessage();

        new StubEnvelopeMapper(new StubEndpoint()).MapEnvelopeToOutgoing(outgoing, written);

        written.Headers[EnvelopeConstants.ExecutionTimeKey].ShouldBe(TheTimestamp.ToString(LegacyFormat));
    }

    /// <summary>
    ///     Documents why the parse is ordered the way it is, and why the legacy format needed a bespoke reader in
    ///     the first place: nothing standard reads it back.
    /// </summary>
    [Fact]
    public void the_legacy_format_is_unreadable_by_a_stock_parser()
    {
        var legacy = TheTimestamp.ToString(LegacyFormat);

        DateTimeOffset.TryParse(legacy, out _).ShouldBeFalse();

        // ...which is exactly why TryParseTimestamp cannot be a single loose parse
        EnvelopeMapper<StubTransportMessage, StubTransportMessage>
            .TryParseTimestamp(legacy, out var parsed).ShouldBeTrue();
        parsed.ToUniversalTime().ShouldBe(TheTimestamp);
    }
}
