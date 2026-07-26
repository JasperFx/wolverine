using CoreTests.Transports;
using JasperFx.Core;
using Shouldly;
using Wolverine.Runtime.Serialization;
using Wolverine.Transports;
using Xunit;

namespace CoreTests.Serialization;

/// <summary>
///     Regression coverage for GH-3613 (and the unfinished half of GH-1716).
///     Wolverine has two writers for the same timestamp keys. The binary envelope format
///     (<see cref="EnvelopeSerializer" />) writes round-trippable <c>"o"</c> values, while
///     <see cref="EnvelopeMapper{TIncoming,TOutgoing}" /> writes transport headers in a custom
///     <c>"yyyy-MM-dd HH:mm:ss:ffffff Z"</c> format that neither <c>DateTime.Parse</c> nor
///     <c>DateTimeOffset.TryParse</c> can read back. <see cref="EnvelopeSerializer.ReadDataElement" />
///     sees values from both sources, and used to blow up on <c>deliver-by</c> (or silently drop
///     <c>time-to-send</c> / <c>keep-until</c>) when handed a mapper-written value.
/// </summary>
public class envelope_timestamp_header_formats_3613
{
    // The value from the GH-3613 report, which is exactly what EnvelopeMapper writes.
    private const string MapperFormattedValue = "2026-07-30 18:25:40:482802 Z";

    // ...which carries six fractional-second digits: 0.482802s == 4,828,020 ticks.
    private static readonly DateTimeOffset TheExpectedTime =
        new DateTimeOffset(2026, 7, 30, 18, 25, 40, TimeSpan.Zero).AddTicks(4828020);

    [Fact]
    public void read_deliver_by_written_in_the_transport_header_format()
    {
        var envelope = new Envelope();

        EnvelopeSerializer.ReadDataElement(envelope, EnvelopeConstants.DeliverByKey, MapperFormattedValue);

        envelope.DeliverBy!.Value.ToUniversalTime().ShouldBe(TheExpectedTime);
    }

    [Fact]
    public void read_scheduled_time_written_in_the_transport_header_format()
    {
        var envelope = new Envelope();

        EnvelopeSerializer.ReadDataElement(envelope, EnvelopeConstants.ExecutionTimeKey, MapperFormattedValue);

        envelope.ScheduledTime!.Value.ToUniversalTime().ShouldBe(TheExpectedTime);
    }

    [Fact]
    public void read_keep_until_written_in_the_transport_header_format()
    {
        var envelope = new Envelope();

        EnvelopeSerializer.ReadDataElement(envelope, EnvelopeConstants.KeepUntilKey, MapperFormattedValue);

        envelope.KeepUntil!.Value.ToUniversalTime().ShouldBe(TheExpectedTime);
    }

    /// <summary>
    ///     The core of the bug: a scheduled retry envelope carrying a mapper-formatted
    ///     <c>deliver-by</c> has to survive a full trip through the durable/scheduled wire format.
    ///     Before the fix the read threw, and every caller that treats a deserialization failure as
    ///     "corrupt, discard" -- the Redis scheduled-set sweeps among them -- destroyed the message.
    /// </summary>
    [Fact]
    public void a_mapper_formatted_deliver_by_header_survives_a_full_round_trip()
    {
        var outgoing = new Envelope
        {
            Data = [1, 2, 3],
            Destination = "tcp://localhost:2222/incoming".ToUri(),
            MessageType = "some-message"
        };

        // A custom IEnvelopeMapper copying raw broker headers, or interop with another Wolverine
        // endpoint, lands the mapper's own formatting in Headers under the reserved key.
        outgoing.Headers[EnvelopeConstants.DeliverByKey] = MapperFormattedValue;

        Should.NotThrow(() => EnvelopeSerializer.Deserialize(EnvelopeSerializer.Serialize(outgoing)));
    }

    [Fact]
    public void an_unreadable_timestamp_does_not_fail_the_whole_envelope_read()
    {
        var envelope = new Envelope();

        Should.NotThrow(() =>
            EnvelopeSerializer.ReadDataElement(envelope, EnvelopeConstants.DeliverByKey, "not a timestamp at all"));

        envelope.DeliverBy.ShouldBeNull();
    }

    [Fact]
    public void deliver_by_is_not_read_twice()
    {
        var envelope = new Envelope { DeliverBy = TheExpectedTime };

        EnvelopeSerializer.ReadDataElement(envelope, EnvelopeConstants.DeliverByKey,
            "2020-01-01 01:01:01:000000 Z");

        envelope.DeliverBy!.Value.ShouldBe(TheExpectedTime);
    }

    [Fact]
    public void the_round_trippable_format_still_reads()
    {
        var envelope = new Envelope();

        EnvelopeSerializer.ReadDataElement(envelope, EnvelopeConstants.DeliverByKey,
            TheExpectedTime.ToString("o"));

        envelope.DeliverBy!.Value.ToUniversalTime().ShouldBe(TheExpectedTime);
    }

    /// <summary>
    ///     Locks the writer and the reader together rather than trusting a hand-copied format string:
    ///     whatever a real <see cref="EnvelopeMapper{TIncoming,TOutgoing}" /> puts on the wire for a
    ///     timestamp key must be readable by <see cref="EnvelopeSerializer.ReadDataElement" />.
    /// </summary>
    [Theory]
    [InlineData(EnvelopeConstants.DeliverByKey)]
    [InlineData(EnvelopeConstants.ExecutionTimeKey)]
    public void what_a_real_envelope_mapper_writes_is_what_the_reader_accepts(string key)
    {
        var outgoing = new Envelope
        {
            DeliverBy = TheExpectedTime,
            ScheduledTime = TheExpectedTime,
            MessageType = "some-message",
            Data = [1, 2, 3]
        };

        var written = new StubTransportMessage();
        new StubEnvelopeMapper(new StubEndpoint()).MapEnvelopeToOutgoing(outgoing, written);

        written.Headers.ContainsKey(key).ShouldBeTrue();
        var onTheWire = written.Headers[key]!;

        var incoming = new Envelope();
        EnvelopeSerializer.ReadDataElement(incoming, key, onTheWire);

        var read = key == EnvelopeConstants.DeliverByKey ? incoming.DeliverBy : incoming.ScheduledTime;
        read.ShouldNotBeNull($"'{key}' was written as '{onTheWire}' but the reader could not read it back");
        read!.Value.ToUniversalTime().ShouldBe(TheExpectedTime);
    }
}
