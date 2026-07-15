using System.Text.Json;
using Confluent.Kafka;
using Shouldly;
using Wolverine.Kafka.Internals;
using Wolverine.Runtime.Serialization;

namespace Wolverine.Kafka.Tests;

public class JsonOnlyMapperTimestampTests
{
    [Theory]
    [InlineData(TimestampType.CreateTime)]
    [InlineData(TimestampType.LogAppendTime)]
    public void maps_available_kafka_timestamp_to_envelope_sent_at(TimestampType timestampType)
    {
        var timestamp = new DateTime(2026, 7, 13, 11, 15, 30, DateTimeKind.Utc);
        var incoming = new Message<string, byte[]>
        {
            Value = [],
            Timestamp = new Timestamp(timestamp, timestampType)
        };
        var envelope = new Envelope { SentAt = DateTimeOffset.MinValue };

        buildMapper().MapIncomingToEnvelope(envelope, incoming);

        envelope.SentAt.ShouldBe(new DateTimeOffset(timestamp));
    }

    [Fact]
    public void leaves_envelope_sent_at_unchanged_when_kafka_timestamp_is_unavailable()
    {
        var originalSentAt = new DateTimeOffset(2026, 7, 13, 10, 0, 0, TimeSpan.Zero);
        var incoming = new Message<string, byte[]>
        {
            Value = [],
            Timestamp = Timestamp.Default
        };
        var envelope = new Envelope { SentAt = originalSentAt };

        buildMapper().MapIncomingToEnvelope(envelope, incoming);

        envelope.SentAt.ShouldBe(originalSentAt);
    }

    [Fact]
    public void all_reserved_kafka_headers_leave_every_typed_property_alone()
    {
        var headers = new Headers();
        foreach (var key in EnvelopeSerializer.ReservedHeaderKeys)
        {
            headers.Add(key, "hijacked"u8.ToArray());
        }

        var timestamp = new DateTime(2026, 7, 13, 11, 15, 30, DateTimeKind.Utc);
        var incoming = new Message<string, byte[]>
        {
            Value = [],
            Timestamp = new Timestamp(timestamp, TimestampType.CreateTime),
            Headers = headers
        };
        var id = Guid.NewGuid();
        var destination = new Uri("kafka://localhost/expected");
        var expected = new Envelope
        {
            Id = id,
            Destination = destination,
            TenantId = "real-tenant",
            SagaId = "real-saga"
        };
        var actual = new Envelope
        {
            Id = id,
            Destination = destination,
            TenantId = "real-tenant",
            SagaId = "real-saga"
        };
        var mapper = buildMapper(typeof(JsonOnlyMapperTimestampTests));

        mapper.MapIncomingToEnvelope(expected, new Message<string, byte[]>
        {
            Value = [],
            Timestamp = incoming.Timestamp
        });
        mapper.MapIncomingToEnvelope(actual, incoming);

        actual.TenantId.ShouldBe(expected.TenantId);
        actual.SagaId.ShouldBe(expected.SagaId);
        actual.MessageType.ShouldBe(expected.MessageType);
        actual.Id.ShouldBe(expected.Id);
        actual.Destination.ShouldBe(expected.Destination);
        actual.SentAt.ShouldBe(expected.SentAt);
        actual.Headers.ShouldBeEmpty();
    }

    [Fact]
    public void non_reserved_custom_headers_still_map_to_envelope()
    {
        var incoming = new Message<string, byte[]>
        {
            Value = [],
            Timestamp = Timestamp.Default,
            Headers = new Headers
            {
                { "name", "Jeremy"u8.ToArray() },
                { "state", "Texas"u8.ToArray() },
                { EnvelopeConstants.TenantIdKey, "hijacked-tenant"u8.ToArray() }
            }
        };
        var envelope = new Envelope();

        buildMapper().MapIncomingToEnvelope(envelope, incoming);

        envelope.Headers["name"].ShouldBe("Jeremy");
        envelope.Headers["state"].ShouldBe("Texas");
        envelope.Headers.ContainsKey(EnvelopeConstants.TenantIdKey).ShouldBeFalse();
    }

    [Fact]
    public void null_custom_header_maps_to_envelope_without_failing()
    {
        var incoming = new Message<string, byte[]>
        {
            Value = [],
            Headers = new Headers
            {
                { "optional-header", null! }
            }
        };
        var envelope = new Envelope();

        buildMapper().MapIncomingToEnvelope(envelope, incoming);

        envelope.Headers.ContainsKey("optional-header").ShouldBeTrue();
        envelope.Headers["optional-header"].ShouldBeNull();
    }

    [Theory]
    [MemberData(nameof(ReservedHeaderKeys))]
    public void each_reserved_kafka_header_is_skipped(string key)
    {
        var incoming = new Message<string, byte[]>
        {
            Value = [],
            Headers = new Headers
            {
                { key, "hijacked"u8.ToArray() }
            }
        };
        var envelope = new Envelope();

        buildMapper().MapIncomingToEnvelope(envelope, incoming);

        envelope.Headers.ContainsKey(key).ShouldBeFalse();
    }

    [Fact]
    public void does_not_overwrite_existing_envelope_headers()
    {
        var incoming = new Message<string, byte[]>
        {
            Value = [],
            Timestamp = Timestamp.Default,
            Headers = new Headers
            {
                { "color-source", "from-kafka"u8.ToArray() }
            }
        };
        var envelope = new Envelope();
        envelope.Headers["color-source"] = "already-set";

        buildMapper().MapIncomingToEnvelope(envelope, incoming);

        envelope.Headers["color-source"].ShouldBe("already-set");
    }

    [Fact]
    public void leaves_envelope_headers_untouched_when_incoming_has_none()
    {
        var incoming = new Message<string, byte[]>
        {
            Value = [],
            Timestamp = Timestamp.Default
        };
        var envelope = new Envelope();

        buildMapper().MapIncomingToEnvelope(envelope, incoming);

        envelope.Headers.ShouldBeEmpty();
    }

    public static IEnumerable<object[]> ReservedHeaderKeys()
    {
        return EnvelopeSerializer.ReservedHeaderKeys.Select(key => new object[] { key });
    }

    private static JsonOnlyMapper buildMapper(Type? messageType = null)
    {
        var topic = new KafkaTopic(
            new KafkaTransport(),
            "timestamp-mapping",
            Configuration.EndpointRole.Application)
        {
            MessageType = messageType
        };

        return new JsonOnlyMapper(topic, new JsonSerializerOptions());
    }
}
