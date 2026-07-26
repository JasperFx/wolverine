using Shouldly;
using Wolverine.Runtime.Serialization;
using Wolverine.Transports;
using Xunit;

namespace CoreTests.Transports;

/// <summary>
///     GH-3645, step ONE. Wolverine has two writers for the same timestamp header keys:
///     <see cref="EnvelopeMapper{TIncoming,TOutgoing}" /> emits
///     <see cref="EnvelopeConstants.TransportHeaderDateTimeFormat" /> (<c>"yyyy-MM-dd HH:mm:ss:ffffff Z"</c>),
///     while <see cref="EnvelopeSerializer" /> emits round-trippable <c>"o"</c>. The mapper's reader only ever
///     accepted its own format, so an <c>"o"</c> value — from the serializer, from a non-Wolverine producer, or
///     from a future Wolverine that has flipped its writers — bound as <c>default</c>/<c>null</c> silently.
///     That silent-null is precisely the failure mode of GH-1716 and GH-3613.
///
///     <para>The reader now accepts both. The <b>writer is deliberately unchanged</b> and pinned below: readers
///     have to tolerate both formats in a shipped release before the writers can flip, or a newer sender talking
///     to an older receiver reintroduces the silent null.</para>
/// </summary>
public class envelope_mapper_reads_both_timestamp_formats_3645
{
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
        // Unchanged behaviour: this is still what the mapper writes.
        var value = TheTimestamp.ToString(EnvelopeConstants.TransportHeaderDateTimeFormat);

        readBack(value, EnvelopeConstants.ExecutionTimeKey)
            .ScheduledTime!.Value.ToUniversalTime().ShouldBe(TheTimestamp);
    }

    [Fact]
    public void reads_the_round_trippable_format()
    {
        // New: an "o" value used to bind null.
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

        // Take what the binary format put on the wire for this key and feed it to the mapper's reader.
        readBack(roundTripped.ScheduledTime!.Value.ToString("o"), EnvelopeConstants.ExecutionTimeKey)
            .ScheduledTime!.Value.ToUniversalTime().ShouldBe(TheTimestamp);
    }

    /// <summary>
    ///     Pins step ONE as step one. The writer must still emit the legacy format until a later release, so
    ///     that a newer sender cannot hand an older receiver something it cannot parse. When GH-3645 step two
    ///     lands, this assertion flips to <c>"o"</c> — deliberately, not by accident.
    /// </summary>
    [Fact]
    public void the_writer_still_emits_the_legacy_format()
    {
        var outgoing = new Envelope { ScheduledTime = TheTimestamp };
        var written = new StubTransportMessage();

        new StubEnvelopeMapper(new StubEndpoint()).MapEnvelopeToOutgoing(outgoing, written);

        written.Headers[EnvelopeConstants.ExecutionTimeKey]
            .ShouldBe(TheTimestamp.ToString(EnvelopeConstants.TransportHeaderDateTimeFormat));
    }
}
