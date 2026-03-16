using Newtonsoft.Json;

namespace Wolverine.CosmosDb.Internals.Transport;

internal class ControlMessage
{
    [JsonProperty("id")] public string Id { get; set; } = string.Empty;

    [JsonProperty("docType")] public string DocType { get; set; } = DocumentTypes.ControlMessage;

    [JsonProperty("partitionKey")] public string PartitionKey { get; set; } = string.Empty;

    [JsonProperty("nodeId")] public string NodeId { get; set; } = string.Empty;

    [JsonProperty("messageType")] public string MessageType { get; set; } = string.Empty;

    [JsonProperty("body")] public byte[] Body { get; set; } = [];

    [JsonProperty("expires")] public long Expires { get; set; }

    [JsonProperty("posted")] public long Posted { get; set; }

    public static string PartitionKeyFor(Guid nodeId) =>
        $"{DocumentTypes.ControlPartitionPrefix}{nodeId}";
}
