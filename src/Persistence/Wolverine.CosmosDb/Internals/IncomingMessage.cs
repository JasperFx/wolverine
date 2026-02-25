using Newtonsoft.Json;
using Wolverine.Runtime.Serialization;

namespace Wolverine.CosmosDb.Internals;

public class IncomingMessage
{
    public IncomingMessage()
    {
    }

    public IncomingMessage(Envelope envelope, CosmosDbMessageStore store)
    {
        Id = store.IdentityFor(envelope);
        EnvelopeId = envelope.Id;
        Status = envelope.Status;
        OwnerId = envelope.OwnerId;
        ExecutionTime = envelope.ScheduledTime?.ToUniversalTime();
        Attempts = envelope.Attempts;
        Body = envelope.Status == EnvelopeStatus.Handled ? [] : EnvelopeSerializer.Serialize(envelope);
        MessageType = envelope.MessageType!;
        ReceivedAt = envelope.Destination?.ToString();
        PartitionKey = envelope.Destination?.ToString() ?? DocumentTypes.SystemPartition;
    }

    [JsonProperty("id")]
    public string Id { get; set; } = string.Empty;

    [JsonProperty("docType")]
    public string DocType { get; set; } = DocumentTypes.Incoming;

    [JsonProperty("partitionKey")]
    public string PartitionKey { get; set; } = string.Empty;

    [JsonProperty("envelopeId")]
    public Guid EnvelopeId { get; set; }

    [JsonProperty("status")]
    public EnvelopeStatus Status { get; set; } = EnvelopeStatus.Incoming;

    [JsonProperty("ownerId")]
    public int OwnerId { get; set; }

    [JsonProperty("executionTime")]
    public DateTimeOffset? ExecutionTime { get; set; }

    [JsonProperty("attempts")]
    public int Attempts { get; set; }

    [JsonProperty("body")]
    public byte[] Body { get; set; } = [];

    [JsonProperty("messageType")]
    public string MessageType { get; set; } = string.Empty;

    [JsonProperty("receivedAt")]
    public string? ReceivedAt { get; set; }

    [JsonProperty("keepUntil")]
    public DateTimeOffset? KeepUntil { get; set; }

    [JsonProperty("_etag")]
    public string? ETag { get; set; }

    public Envelope Read()
    {
        Envelope envelope;
        if (Body == null || Body.Length == 0)
        {
            envelope = new Envelope
            {
                Id = EnvelopeId,
                MessageType = MessageType,
                Destination = ReceivedAt != null ? new Uri(ReceivedAt) : null,
                Data = []
            };
        }
        else
        {
            envelope = EnvelopeSerializer.Deserialize(Body);
        }

        envelope.Id = EnvelopeId;
        envelope.OwnerId = OwnerId;
        envelope.Status = Status;
        envelope.Attempts = Attempts;
        envelope.ScheduledTime = ExecutionTime;
        return envelope;
    }
}
