using System.Text;
using Dekaf.Consumer;
using Shouldly;
using Wolverine.Configuration;
using Wolverine.Kafka.Internals;
using Wolverine.Util;

namespace Wolverine.Kafka.Tests;

// Every read of ConsumeResult.Headers copies all of the record's headers, so the reserved-header readers work from
// the envelope's copy and only go back to the record for a header copied with a null value. No broker required.
public class kafka_incoming_header_lookups
{
    private readonly CountingMapper theMapper =
        new(new KafkaTopic(new KafkaTransport(), "header-lookups", EndpointRole.Application));

    private static KeyValuePair<string, byte[]?> header(string key, string? value) =>
        new(key, value == null ? null : Encoding.UTF8.GetBytes(value));

    private Envelope map(params KeyValuePair<string, byte[]?>[] headers)
    {
        var envelope = new Envelope();
        theMapper.MapIncomingToEnvelope(envelope, TestRecords.Incoming("header-lookups", "key", "{}"u8.ToArray(), headers));
        return envelope;
    }

    [Fact]
    public void reserved_headers_on_the_record_are_read_from_the_copy()
    {
        var envelope = map(header(EnvelopeConstants.TenantIdKey, "red"), header(EnvelopeConstants.CorrelationIdKey, "c-1"));

        envelope.TenantId.ShouldBe("red");
        envelope.CorrelationId.ShouldBe("c-1");
        theMapper.RecordReads.ShouldBe(0);
    }

    [Fact]
    public void reserved_headers_missing_from_the_record_are_not_looked_up_on_it()
    {
        var envelope = map(header(EnvelopeConstants.MessageTypeKey, "scratch"));

        envelope.TenantId.ShouldBeNull();
        envelope.Attempts.ShouldBe(0);
        theMapper.RecordReads.ShouldBe(0);
    }

    [Fact]
    public void a_record_without_headers_is_not_looked_up()
    {
        var envelope = map();

        envelope.TenantId.ShouldBeNull();
        theMapper.RecordReads.ShouldBe(0);
    }

    [Fact]
    public void the_last_value_of_a_repeated_header_wins()
    {
        var envelope = map(header(EnvelopeConstants.TenantIdKey, "red"), header(EnvelopeConstants.TenantIdKey, "blue"));

        envelope.TenantId.ShouldBe("blue");
    }

    [Fact]
    public void a_repeated_header_whose_last_value_is_null_falls_back_to_the_earlier_value()
    {
        var envelope = map(header(EnvelopeConstants.TenantIdKey, "red"), header(EnvelopeConstants.TenantIdKey, null));

        envelope.TenantId.ShouldBe("red");
        theMapper.RecordReads.ShouldBe(1);
    }

    [Fact]
    public void headers_read_once_find_the_last_non_null_value()
    {
        var incoming = TestRecords.Incoming("header-lookups", "key", "{}"u8.ToArray(),
            [header("a", "one"), header("a", "two"), header("a", null)]);
        var headers = incoming.Headers;

        headers.TryGetLastHeaderValue("a", skipNullValues: true, out var value).ShouldBeTrue();
        value.ShouldBe("two");

        headers.TryGetLastHeaderValue("a", skipNullValues: false, out value).ShouldBeTrue();
        value.ShouldBeNull();

        headers.TryGetLastHeaderValue("missing", skipNullValues: true, out _).ShouldBeFalse();
    }

    private class CountingMapper(Endpoint endpoint) : KafkaEnvelopeMapper(endpoint)
    {
        public int RecordReads { get; private set; }

        protected override bool tryReadIncomingHeader(ConsumeResult<string, byte[]> incoming, string key, out string value)
        {
            RecordReads++;
            return base.tryReadIncomingHeader(incoming, key, out value);
        }
    }
}
