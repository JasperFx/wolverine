using JasperFx.Core.Reflection;
using Newtonsoft.Json;
using Wolverine.Persistence.Durability;
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
        ExceptionType = exception?.GetType().FullNameInCode();
        ExceptionMessage = exception?.Message;
        Body = EnvelopeSerializer.Serialize(envelope);
        PartitionKey = DocumentTypes.DeadLetterPartition;
    }

    [JsonProperty("id")]
    public string Id { get; set; } = string.Empty;

    [JsonProperty("docType")]
    public string DocType { get; set; } = DocumentTypes.DeadLetter;

    [JsonProperty("partitionKey")]
    public string PartitionKey { get; set; } = DocumentTypes.DeadLetterPartition;

    [JsonProperty("envelopeId")]
    public Guid EnvelopeId { get; set; }

    [JsonProperty("messageType")]
    public string? MessageType { get; set; }

    [JsonProperty("receivedAt")]
    public string? ReceivedAt { get; set; }

    [JsonProperty("sentAt")]
    public DateTimeOffset? SentAt { get; set; }

    [JsonProperty("scheduledTime")]
    public DateTimeOffset? ScheduledTime { get; set; }

    [JsonProperty("source")]
    public string? Source { get; set; }

    [JsonProperty("exceptionType")]
    public string? ExceptionType { get; set; }

    [JsonProperty("exceptionMessage")]
    public string? ExceptionMessage { get; set; }

    [JsonProperty("replayable")]
    public bool Replayable { get; set; }

    [JsonProperty("body")]
    public byte[] Body { get; set; } = [];

    [JsonProperty("expirationTime")]
    public DateTimeOffset ExpirationTime { get; set; }

    [JsonProperty("_etag")]
    public string? ETag { get; set; }

    public DeadLetterEnvelope ToEnvelope()
    {
        var envelope = EnvelopeSerializer.Deserialize(Body);
        return new DeadLetterEnvelope(EnvelopeId, ScheduledTime, envelope, MessageType ?? "",
            ReceivedAt ?? "", Source ?? "", ExceptionType ?? "", ExceptionMessage ?? "",
            SentAt ?? DateTimeOffset.MinValue, Replayable);
    }
}
