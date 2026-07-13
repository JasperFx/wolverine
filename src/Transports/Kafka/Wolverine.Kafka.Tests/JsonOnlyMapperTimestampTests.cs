using System.Text.Json;
using Confluent.Kafka;
using Shouldly;
using Wolverine.Kafka.Internals;

namespace Wolverine.Kafka.Tests;

public class JsonOnlyMapperTimestampTests
{
    [Theory]
    [InlineData(TimestampType.CreateTime)]
    [InlineData(TimestampType.LogAppendTime)]
    public void MapsAvailableKafkaTimestampToEnvelopeSentAt(TimestampType timestampType)
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
    public void LeavesEnvelopeSentAtUnchangedWhenKafkaTimestampIsUnavailable()
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
    public void CopiesIncomingKafkaHeadersToEnvelope()
    {
        var incoming = new Message<string, byte[]>
        {
            Value = [],
            Timestamp = Timestamp.Default,
            Headers = new Headers
            {
                { "tenant-id", "acme"u8.ToArray() },
                { "correlation", "abc-123"u8.ToArray() }
            }
        };
        var envelope = new Envelope();

        buildMapper().MapIncomingToEnvelope(envelope, incoming);

        envelope.Headers["tenant-id"].ShouldBe("acme");
        envelope.Headers["correlation"].ShouldBe("abc-123");
    }

    [Fact]
    public void DoesNotOverwriteExistingEnvelopeHeaders()
    {
        var incoming = new Message<string, byte[]>
        {
            Value = [],
            Timestamp = Timestamp.Default,
            Headers = new Headers
            {
                { "tenant-id", "from-kafka"u8.ToArray() }
            }
        };
        var envelope = new Envelope();
        envelope.Headers["tenant-id"] = "already-set";

        buildMapper().MapIncomingToEnvelope(envelope, incoming);

        envelope.Headers["tenant-id"].ShouldBe("already-set");
    }

    [Fact]
    public void LeavesEnvelopeHeadersUntouchedWhenIncomingHasNone()
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

    private static JsonOnlyMapper buildMapper()
    {
        var topic = new KafkaTopic(
            new KafkaTransport(),
            "timestamp-mapping",
            Configuration.EndpointRole.Application);

        return new JsonOnlyMapper(topic, new JsonSerializerOptions());
    }
}
