using System.Text.Json.Serialization;
using Newtonsoft.Json;
using Wolverine.Runtime.Serialization;

namespace Wolverine.CosmosDb.Internals;

public class OutgoingMessage
{
    public OutgoingMessage()
    {
    }

    public OutgoingMessage(Envelope envelope)
    {
        Id = $"outgoing|{envelope.Id}";
        EnvelopeId = envelope.Id;
        OwnerId = envelope.OwnerId;
        Attempts = envelope.Attempts;
        Body = EnvelopeSerializer.Serialize(envelope);
        MessageType = envelope.MessageType!;
        Destination = envelope.Destination?.ToString();
        DeliverBy = envelope.DeliverBy?.ToUniversalTime();
        PartitionKey = envelope.Destination?.ToString() ?? DocumentTypes.SystemPartition;
    }

    [JsonProperty("id")]
    [JsonPropertyName("id")]
    public string Id { get; set; } = string.Empty;

    [JsonProperty("docType")]
    [JsonPropertyName("docType")]
    public string DocType { get; set; } = DocumentTypes.Outgoing;

    [JsonProperty("partitionKey")]
    [JsonPropertyName("partitionKey")]
    public string PartitionKey { get; set; } = string.Empty;

    [JsonProperty("envelopeId")]
    [JsonPropertyName("envelopeId")]
    public Guid EnvelopeId { get; set; }

    [JsonProperty("ownerId")]
    [JsonPropertyName("ownerId")]
    public int OwnerId { get; set; }

    [JsonProperty("destination")]
    [JsonPropertyName("destination")]
    public string? Destination { get; set; }

    [JsonProperty("deliverBy")]
    [JsonPropertyName("deliverBy")]
    public DateTimeOffset? DeliverBy { get; set; }

    [JsonProperty("body")]
    [JsonPropertyName("body")]
    public byte[] Body { get; set; } = [];

    [JsonProperty("attempts")]
    [JsonPropertyName("attempts")]
    public int Attempts { get; set; }

    [JsonProperty("messageType")]
    [JsonPropertyName("messageType")]
    public string MessageType { get; set; } = string.Empty;

    [JsonProperty("_etag")]
    [JsonPropertyName("_etag")]
    public string? ETag { get; set; }

    public Envelope Read()
    {
        var envelope = EnvelopeSerializer.Deserialize(Body);
        envelope.OwnerId = OwnerId;
        envelope.Attempts = Attempts;
        return envelope;
    }
}
