using System.Text.Json.Serialization;
using Newtonsoft.Json;

namespace Wolverine.CosmosDb.Internals.Transport;

internal class ControlMessage
{
    [JsonProperty("id")]
    [JsonPropertyName("id")]
    public string Id { get; set; } = string.Empty;

    [JsonProperty("docType")]
    [JsonPropertyName("docType")]
    public string DocType { get; set; } = DocumentTypes.ControlMessage;

    [JsonProperty("partitionKey")]
    [JsonPropertyName("partitionKey")]
    public string PartitionKey { get; set; } = string.Empty;

    [JsonProperty("nodeId")]
    [JsonPropertyName("nodeId")]
    public string NodeId { get; set; } = string.Empty;

    [JsonProperty("messageType")]
    [JsonPropertyName("messageType")]
    public string MessageType { get; set; } = string.Empty;

    [JsonProperty("body")]
    [JsonPropertyName("body")]
    public byte[] Body { get; set; } = [];

    [JsonProperty("expires")]
    [JsonPropertyName("expires")]
    public long Expires { get; set; }

    [JsonProperty("posted")]
    [JsonPropertyName("posted")]
    public long Posted { get; set; }

    public static string PartitionKeyFor(Guid nodeId) =>
        $"{DocumentTypes.ControlPartitionPrefix}{nodeId}";
}
