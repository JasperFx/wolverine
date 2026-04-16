using System.Text.Json;
using Newtonsoft.Json;
using Shouldly;
using Wolverine.CosmosDb.Internals;
using Wolverine.Runtime.Agents;
using JsonSerializer = System.Text.Json.JsonSerializer;

namespace CosmosDbTests;

public class document_serialization
{
    /// <summary>
    /// Verifies that both System.Text.Json and Newtonsoft.Json serialize the
    /// partitionKey property with the correct camelCase name. A mismatch would
    /// cause CosmosDB 400/1001 errors (partition key extracted from document
    /// doesn't match the header value).
    /// </summary>
    [Fact]
    public void all_document_types_serialize_partitionKey_consistently()
    {
        var docs = new object[]
        {
            new CosmosWolverineNode
            {
                Id = "node|test",
                NodeId = Guid.NewGuid().ToString(),
                PartitionKey = "system"
            },
            new CosmosAgentAssignment
            {
                Id = "agent|test",
                NodeId = Guid.NewGuid().ToString(),
                AgentUri = "fake://agent",
                PartitionKey = "system"
            },
            new CosmosNodeSequence
            {
                Id = "node-sequence|sequence",
                PartitionKey = "system",
                Count = 1
            },
            new CosmosAgentRestriction
            {
                Id = "restriction|test",
                AgentUri = "fake://agent",
                Type = AgentRestrictionType.Pinned,
                NodeNumber = 1,
                PartitionKey = "system"
            },
            new CosmosNodeRecord
            {
                Id = "record|test",
                NodeNumber = 1,
                RecordType = NodeRecordType.AssignmentChanged,
                Timestamp = DateTimeOffset.UtcNow,
                PartitionKey = "system"
            },
            new CosmosDistributedLock
            {
                Id = "lock|test",
                NodeId = Guid.NewGuid().ToString(),
                ExpirationTime = DateTimeOffset.UtcNow.AddMinutes(5),
                PartitionKey = "system"
            },
            new IncomingMessage
            {
                Id = "incoming|test",
                EnvelopeId = Guid.NewGuid(),
                PartitionKey = "system"
            },
            new OutgoingMessage
            {
                Id = "outgoing|test",
                EnvelopeId = Guid.NewGuid(),
                PartitionKey = "system"
            },
            new DeadLetterMessage
            {
                Id = "deadletter|test",
                EnvelopeId = Guid.NewGuid(),
                PartitionKey = "deadletter"
            }
        };

        foreach (var doc in docs)
        {
            var typeName = doc.GetType().Name;

            // System.Text.Json - verify camelCase partitionKey via JSON document parsing
            var stjJson = JsonSerializer.Serialize(doc, doc.GetType());
            using (var jsonDoc = JsonDocument.Parse(stjJson))
            {
                jsonDoc.RootElement.TryGetProperty("partitionKey", out _).ShouldBeTrue(
                    $"{typeName}: STJ should have 'partitionKey' property");
                jsonDoc.RootElement.TryGetProperty("PartitionKey", out _).ShouldBeFalse(
                    $"{typeName}: STJ should NOT have 'PartitionKey' property");
            }

            // Newtonsoft.Json - verify camelCase partitionKey via JObject parsing
            var newtonsoftJson = JsonConvert.SerializeObject(doc);
            var jObj = Newtonsoft.Json.Linq.JObject.Parse(newtonsoftJson);
            jObj.ContainsKey("partitionKey").ShouldBeTrue(
                $"{typeName}: Newtonsoft should have 'partitionKey' property");
            jObj.ContainsKey("PartitionKey").ShouldBeFalse(
                $"{typeName}: Newtonsoft should NOT have 'PartitionKey' property");
        }
    }

    [Fact]
    public void all_document_types_serialize_id_consistently()
    {
        var docs = new object[]
        {
            new CosmosWolverineNode { Id = "node|test" },
            new CosmosAgentAssignment { Id = "agent|test" },
            new CosmosNodeSequence { Id = "seq|test" },
            new CosmosDistributedLock { Id = "lock|test" },
            new IncomingMessage { Id = "in|test" },
            new OutgoingMessage { Id = "out|test" },
            new DeadLetterMessage { Id = "dl|test" }
        };

        foreach (var doc in docs)
        {
            var stjJson = JsonSerializer.Serialize(doc, doc.GetType());
            stjJson.ShouldContain("\"id\"");

            var newtonsoftJson = JsonConvert.SerializeObject(doc);
            newtonsoftJson.ShouldContain("\"id\"");
        }
    }

    [Fact]
    public void all_document_types_serialize_docType_consistently()
    {
        var docs = new (object Doc, string ExpectedDocType)[]
        {
            (new CosmosWolverineNode(), DocumentTypes.Node),
            (new CosmosAgentAssignment(), DocumentTypes.AgentAssignment),
            (new CosmosNodeSequence(), DocumentTypes.NodeSequence),
            (new CosmosAgentRestriction(), DocumentTypes.AgentRestriction),
            (new CosmosNodeRecord(), DocumentTypes.NodeRecord),
            (new CosmosDistributedLock(), DocumentTypes.Lock),
            (new IncomingMessage(), DocumentTypes.Incoming),
            (new OutgoingMessage(), DocumentTypes.Outgoing),
            (new DeadLetterMessage(), DocumentTypes.DeadLetter)
        };

        foreach (var (doc, expectedDocType) in docs)
        {
            var stjJson = JsonSerializer.Serialize(doc, doc.GetType());
            stjJson.ShouldContain($"\"docType\":\"{expectedDocType}\"");

            var newtonsoftJson = JsonConvert.SerializeObject(doc);
            newtonsoftJson.ShouldContain($"\"docType\":\"{expectedDocType}\"");
        }
    }

    [Fact]
    public void stj_can_roundtrip_cosmos_wolverine_node()
    {
        var original = new CosmosWolverineNode
        {
            Id = "node|abc",
            NodeId = Guid.NewGuid().ToString(),
            Description = "Test node",
            AssignedNodeNumber = 42,
            ControlUri = "http://localhost:5000",
            LastHealthCheck = DateTimeOffset.UtcNow,
            Started = DateTimeOffset.UtcNow.AddHours(-1),
            Version = "1.0.0",
            Capabilities = ["http://capability/1"]
        };

        var json = JsonSerializer.Serialize(original);
        var deserialized = JsonSerializer.Deserialize<CosmosWolverineNode>(json)!;

        deserialized.Id.ShouldBe(original.Id);
        deserialized.NodeId.ShouldBe(original.NodeId);
        deserialized.Description.ShouldBe(original.Description);
        deserialized.AssignedNodeNumber.ShouldBe(original.AssignedNodeNumber);
        deserialized.ControlUri.ShouldBe(original.ControlUri);
        deserialized.PartitionKey.ShouldBe(DocumentTypes.SystemPartition);
        deserialized.DocType.ShouldBe(DocumentTypes.Node);
    }

    [Fact]
    public void newtonsoft_can_roundtrip_cosmos_wolverine_node()
    {
        var original = new CosmosWolverineNode
        {
            Id = "node|abc",
            NodeId = Guid.NewGuid().ToString(),
            Description = "Test node",
            AssignedNodeNumber = 42,
            ControlUri = "http://localhost:5000",
            LastHealthCheck = DateTimeOffset.UtcNow,
            Started = DateTimeOffset.UtcNow.AddHours(-1),
            Version = "1.0.0",
            Capabilities = ["http://capability/1"]
        };

        var json = JsonConvert.SerializeObject(original);
        var deserialized = JsonConvert.DeserializeObject<CosmosWolverineNode>(json)!;

        deserialized.Id.ShouldBe(original.Id);
        deserialized.NodeId.ShouldBe(original.NodeId);
        deserialized.Description.ShouldBe(original.Description);
        deserialized.AssignedNodeNumber.ShouldBe(original.AssignedNodeNumber);
        deserialized.ControlUri.ShouldBe(original.ControlUri);
        deserialized.PartitionKey.ShouldBe(DocumentTypes.SystemPartition);
        deserialized.DocType.ShouldBe(DocumentTypes.Node);
    }

    [Fact]
    public void stj_and_newtonsoft_produce_same_property_names()
    {
        var doc = new CosmosWolverineNode
        {
            Id = "node|test",
            NodeId = Guid.NewGuid().ToString(),
            Description = "test",
            AssignedNodeNumber = 1
        };

        var stjJson = JsonSerializer.Serialize(doc);
        var newtonsoftJson = JsonConvert.SerializeObject(doc);

        // Parse both and compare property names
        using var stjDoc = JsonDocument.Parse(stjJson);
        var stjProps = stjDoc.RootElement.EnumerateObject()
            .Select(p => p.Name).OrderBy(n => n).ToList();

        var newtonsoftObj = Newtonsoft.Json.Linq.JObject.Parse(newtonsoftJson);
        var newtonsoftProps = newtonsoftObj.Properties()
            .Select(p => p.Name).OrderBy(n => n).ToList();

        stjProps.ShouldBe(newtonsoftProps);
    }
}
