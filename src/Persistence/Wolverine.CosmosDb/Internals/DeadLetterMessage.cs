using System.Text.Json.Serialization;
using JasperFx.Core.Reflection;
using Newtonsoft.Json;
using Wolverine.Persistence.Durability;
using Wolverine.Runtime;
using Wolverine.Runtime.Serialization;

namespace Wolverine.CosmosDb.Internals;

public class DeadLetterMessage
{
    public DeadLetterMessage()
    {
    }

    public DeadLetterMessage(Envelope envelope, Exception? exception)
    {
        Id = $"deadletter|{envelope.Id}";
        EnvelopeId = envelope.Id;
        MessageType = envelope.MessageType;
        ReceivedAt = envelope.Destination?.ToString();
        SentAt = envelope.SentAt;
        ScheduledTime = envelope.ScheduledTime;
        Source = envelope.Source;
        ExceptionType = exception.DeadLetterExceptionType();
        ExceptionMessage = exception.DeadLetterExceptionMessage();
        Body = EnvelopeSerializer.Serialize(envelope);
        PartitionKey = DocumentTypes.DeadLetterPartition;
    }

    [JsonProperty("id")]
    [JsonPropertyName("id")]
    public string Id { get; set; } = string.Empty;

    [JsonProperty("docType")]
    [JsonPropertyName("docType")]
    public string DocType { get; set; } = DocumentTypes.DeadLetter;

    [JsonProperty("partitionKey")]
    [JsonPropertyName("partitionKey")]
    public string PartitionKey { get; set; } = DocumentTypes.DeadLetterPartition;

    [JsonProperty("envelopeId")]
    [JsonPropertyName("envelopeId")]
    public Guid EnvelopeId { get; set; }

    [JsonProperty("messageType")]
    [JsonPropertyName("messageType")]
    public string? MessageType { get; set; }

    [JsonProperty("receivedAt")]
    [JsonPropertyName("receivedAt")]
    public string? ReceivedAt { get; set; }

    [JsonProperty("sentAt")]
    [JsonPropertyName("sentAt")]
    public DateTimeOffset? SentAt { get; set; }

    [JsonProperty("scheduledTime")]
    [JsonPropertyName("scheduledTime")]
    public DateTimeOffset? ScheduledTime { get; set; }

    [JsonProperty("source")]
    [JsonPropertyName("source")]
    public string? Source { get; set; }

    [JsonProperty("exceptionType")]
    [JsonPropertyName("exceptionType")]
    public string? ExceptionType { get; set; }

    [JsonProperty("exceptionMessage")]
    [JsonPropertyName("exceptionMessage")]
    public string? ExceptionMessage { get; set; }

    [JsonProperty("replayable")]
    [JsonPropertyName("replayable")]
    public bool Replayable { get; set; }

    [JsonProperty("body")]
    [JsonPropertyName("body")]
    public byte[] Body { get; set; } = [];

    [JsonProperty("expirationTime")]
    [JsonPropertyName("expirationTime")]
    public DateTimeOffset ExpirationTime { get; set; }

    [JsonProperty("_etag")]
    [JsonPropertyName("_etag")]
    public string? ETag { get; set; }

    public DeadLetterEnvelope ToEnvelope()
    {
        return new DeadLetterEnvelope(EnvelopeId, ScheduledTime, readBody(), MessageType ?? "",
            ReceivedAt ?? "", Source ?? "", ExceptionType ?? "", ExceptionMessage ?? "",
            SentAt ?? DateTimeOffset.MinValue, Replayable);
    }

    // Same placeholder contract as DatabasePersistence.ReadDeadLetterBodyAsync (CritterWatch#902,
    // GH-3773): a document whose stored body was never written by EnvelopeSerializer — hand-seeded
    // raw JSON, or an incompatible serialization version — must cost the operator that row, not the
    // whole queue read. The scalar fields on this document still describe the poison row.
    private Envelope readBody()
    {
        if (Body is { Length: > 0 })
        {
            try
            {
                return EnvelopeSerializer.Deserialize(Body);
            }
            catch (Exception)
            {
                // fall through to the placeholder
            }
        }

        var envelope = new Envelope
        {
            Id = EnvelopeId,
            MessageType = MessageType,
            Message = new PlaceHolder()
        };

        if (ReceivedAt is not null && Uri.TryCreate(ReceivedAt, UriKind.Absolute, out var destination))
        {
            envelope.Destination = destination;
        }

        return envelope;
    }
}
