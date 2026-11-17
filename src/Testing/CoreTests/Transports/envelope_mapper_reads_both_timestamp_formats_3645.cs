using Shouldly;
using Wolverine.Runtime.Serialization;
using Wolverine.Transports;
using Xunit;

namespace CoreTests.Transports;

/// <summary>GH-3645: standard timestamp writers retain support for reading legacy headers.</summary>
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
        // Persisted headers and older senders still use this format.
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

    [Theory]
    [InlineData(EnvelopeConstants.ExecutionTimeKey)]
    [InlineData(EnvelopeConstants.DeliverByKey)]
    [InlineData(EnvelopeConstants.SentAtKey)]
    public void legacy_headers_remain_readable_after_binary_persistence(string key)
    {
        var envelope = readBack(TheTimestamp.ToString(EnvelopeConstants.TransportHeaderDateTimeFormat), key);
        envelope.MessageType = "some-message";
        envelope.Data = [1, 2, 3];
        var persisted = EnvelopeSerializer.Deserialize(EnvelopeSerializer.Serialize(envelope));
        var actual = key == EnvelopeConstants.ExecutionTimeKey ? persisted.ScheduledTime
            : key == EnvelopeConstants.DeliverByKey ? persisted.DeliverBy : persisted.SentAt;
        actual.ShouldBe(TheTimestamp);
    }

    [Fact]
    public void absent_nullable_timestamps_do_not_write_headers()
    {
        var written = new StubTransportMessage();
        new StubEnvelopeMapper(new StubEndpoint()).MapEnvelopeToOutgoing(new Envelope(), written);
        written.Headers.ContainsKey(EnvelopeConstants.ExecutionTimeKey).ShouldBeFalse();
        written.Headers.ContainsKey(EnvelopeConstants.DeliverByKey).ShouldBeFalse();
    }

    [Theory]
    [InlineData("en-US")]
    [InlineData("fr-FR")]
    [InlineData("ar-SA")]
    public void writers_emit_standard_timestamps_without_losing_precision(string culture)
    {
        var previous = System.Globalization.CultureInfo.CurrentCulture;
        try
        {
            System.Globalization.CultureInfo.CurrentCulture = new System.Globalization.CultureInfo(culture);
            var timestamp = TheTimestamp.AddTicks(7).ToOffset(TimeSpan.FromHours(5.5));
            var outgoing = new Envelope { ScheduledTime = timestamp, DeliverBy = timestamp, SentAt = timestamp };
            var written = new StubTransportMessage();
            var mapper = new StubEnvelopeMapper(new StubEndpoint());
            mapper.MapEnvelopeToOutgoing(outgoing, written);

            foreach (var key in new[] { EnvelopeConstants.ExecutionTimeKey, EnvelopeConstants.DeliverByKey, EnvelopeConstants.SentAtKey })
            {
                written.Headers[key].ShouldBe(timestamp.ToUniversalTime().ToString("o", System.Globalization.CultureInfo.InvariantCulture));
                DateTimeOffset.Parse(written.Headers[key]!, System.Globalization.CultureInfo.InvariantCulture).ShouldBe(timestamp);
            }

            var incoming = new Envelope();
            mapper.MapIncomingToEnvelope(incoming, written);
            incoming.ScheduledTime.ShouldBe(timestamp);
            incoming.DeliverBy.ShouldBe(timestamp);
            incoming.SentAt.ShouldBe(timestamp);
        }
        finally
        {
            System.Globalization.CultureInfo.CurrentCulture = previous;
        }
    }
}
