using System.Net;
using Newtonsoft.Json;
using Microsoft.Azure.Cosmos;
using Wolverine.Runtime.Agents;

namespace Wolverine.CosmosDb.Internals;

public partial class CosmosDbMessageStore : INodeAgentPersistence
{
    async Task INodeAgentPersistence.ClearAllAsync(CancellationToken cancellationToken)
    {
        var nodes = await Nodes.LoadAllNodesAsync(cancellationToken);
        foreach (var node in nodes)
        {
            try
            {
                await _container.DeleteItemAsync<dynamic>(
                    $"node|{node.NodeId}", new PartitionKey(DocumentTypes.SystemPartition),
                    cancellationToken: cancellationToken);
            }
            catch (CosmosException e) when (e.StatusCode == HttpStatusCode.NotFound)
            {
            }

            foreach (var agent in node.ActiveAgents)
            {
                try
                {
                    await _container.DeleteItemAsync<dynamic>(
                        CosmosAgentAssignment.ToId(agent), new PartitionKey(DocumentTypes.SystemPartition),
                        cancellationToken: cancellationToken);
                }
                catch (CosmosException e) when (e.StatusCode == HttpStatusCode.NotFound)
                {
                }
            }
        }
    }

    public async Task<int> PersistAsync(WolverineNode node, CancellationToken cancellationToken)
    {
        var sequenceId = $"{DocumentTypes.NodeSequence}|sequence";
        CosmosNodeSequence? sequence;
        try
        {
            var response = await _container.ReadItemAsync<CosmosNodeSequence>(
                sequenceId, new PartitionKey(DocumentTypes.SystemPartition),
                cancellationToken: cancellationToken);
            sequence = response.Resource;
        }
        catch (CosmosException e) when (e.StatusCode == HttpStatusCode.NotFound)
        {
            sequence = new CosmosNodeSequence { Id = sequenceId };
        }

        node.AssignedNodeNumber = ++sequence.Count;

        await _container.UpsertItemAsync(sequence, new PartitionKey(DocumentTypes.SystemPartition),
            cancellationToken: cancellationToken);

        var nodeDoc = new CosmosWolverineNode(node);
        await _container.UpsertItemAsync(nodeDoc, new PartitionKey(DocumentTypes.SystemPartition),
            cancellationToken: cancellationToken);

        return node.AssignedNodeNumber;
    }

    public async Task DeleteAsync(Guid nodeId, int assignedNodeNumber)
    {
        try
        {
            await _container.DeleteItemAsync<dynamic>(
                $"node|{nodeId}", new PartitionKey(DocumentTypes.SystemPartition));
        }
        catch (CosmosException e) when (e.StatusCode == HttpStatusCode.NotFound)
        {
        }

        // Delete agent assignments for this node
        var queryText =
            "SELECT * FROM c WHERE c.docType = @docType AND c.nodeId = @nodeId";
        var query = new QueryDefinition(queryText)
            .WithParameter("@docType", DocumentTypes.AgentAssignment)
            .WithParameter("@nodeId", nodeId.ToString());

        using var iterator = _container.GetItemQueryIterator<CosmosAgentAssignment>(query,
            requestOptions: new QueryRequestOptions
            {
                PartitionKey = new PartitionKey(DocumentTypes.SystemPartition)
            });

        while (iterator.HasMoreResults)
        {
            var response = await iterator.ReadNextAsync();
            foreach (var assignment in response)
            {
                try
                {
                    await _container.DeleteItemAsync<dynamic>(
                        assignment.Id, new PartitionKey(DocumentTypes.SystemPartition));
                }
                catch (CosmosException e) when (e.StatusCode == HttpStatusCode.NotFound)
                {
                }
            }
        }

        await ReleaseAllOwnershipAsync(assignedNodeNumber);
    }

    public async Task<IReadOnlyList<WolverineNode>> LoadAllNodesAsync(CancellationToken cancellationToken)
    {
        var nodeQuery = new QueryDefinition("SELECT * FROM c WHERE c.docType = @docType")
            .WithParameter("@docType", DocumentTypes.Node);

        var nodes = new List<CosmosWolverineNode>();
        using (var iterator = _container.GetItemQueryIterator<CosmosWolverineNode>(nodeQuery,
                   requestOptions: new QueryRequestOptions
                   {
                       PartitionKey = new PartitionKey(DocumentTypes.SystemPartition)
                   }))
        {
            while (iterator.HasMoreResults)
            {
                var response = await iterator.ReadNextAsync(cancellationToken);
                nodes.AddRange(response);
            }
        }

        var assignmentQuery = new QueryDefinition("SELECT * FROM c WHERE c.docType = @docType")
            .WithParameter("@docType", DocumentTypes.AgentAssignment);

        var assignments = new List<CosmosAgentAssignment>();
        using (var iterator = _container.GetItemQueryIterator<CosmosAgentAssignment>(assignmentQuery,
                   requestOptions: new QueryRequestOptions
                   {
                       PartitionKey = new PartitionKey(DocumentTypes.SystemPartition)
                   }))
        {
            while (iterator.HasMoreResults)
            {
                var response = await iterator.ReadNextAsync(cancellationToken);
                assignments.AddRange(response);
            }
        }

        var result = new List<WolverineNode>();
        foreach (var nodeDoc in nodes)
        {
            var node = nodeDoc.ToWolverineNode();
            node.ActiveAgents.Clear();
            node.ActiveAgents.AddRange(assignments.Where(x => x.NodeId == node.NodeId.ToString())
                .Select(x => new Uri(x.AgentUri)));
            result.Add(node);
        }

        return result;
    }

    public async Task PersistAgentRestrictionsAsync(IReadOnlyList<AgentRestriction> restrictions,
        CancellationToken cancellationToken)
    {
        foreach (var restriction in restrictions)
        {
            if (restriction.Type == AgentRestrictionType.None)
            {
                try
                {
                    await _container.DeleteItemAsync<dynamic>(
                        $"restriction|{restriction.Id}", new PartitionKey(DocumentTypes.SystemPartition),
                        cancellationToken: cancellationToken);
                }
                catch (CosmosException e) when (e.StatusCode == HttpStatusCode.NotFound)
                {
                }
            }
            else
            {
                var doc = new CosmosAgentRestriction
                {
                    Id = $"restriction|{restriction.Id}",
                    AgentUri = restriction.AgentUri.ToString(),
                    Type = restriction.Type,
                    NodeNumber = restriction.NodeNumber
                };
                await _container.UpsertItemAsync(doc, new PartitionKey(DocumentTypes.SystemPartition),
                    cancellationToken: cancellationToken);
            }
        }
    }

    public async Task<NodeAgentState> LoadNodeAgentStateAsync(CancellationToken cancellationToken)
    {
        var nodes = await LoadAllNodesAsync(cancellationToken);

        var restrictionQuery = new QueryDefinition("SELECT * FROM c WHERE c.docType = @docType")
            .WithParameter("@docType", DocumentTypes.AgentRestriction);

        var restrictions = new List<CosmosAgentRestriction>();
        using var iterator = _container.GetItemQueryIterator<CosmosAgentRestriction>(restrictionQuery,
            requestOptions: new QueryRequestOptions
            {
                PartitionKey = new PartitionKey(DocumentTypes.SystemPartition)
            });

        while (iterator.HasMoreResults)
        {
            var response = await iterator.ReadNextAsync(cancellationToken);
            restrictions.AddRange(response);
        }

        var agentRestrictions = restrictions.Select(
            x => new AgentRestriction(Guid.Parse(x.Id.Replace("restriction|", "")),
                new Uri(x.AgentUri), x.Type, x.NodeNumber)
        );

        return new NodeAgentState(nodes, new AgentRestrictions([.. agentRestrictions]));
    }

    public async Task AssignAgentsAsync(Guid nodeId, IReadOnlyList<Uri> agents,
        CancellationToken cancellationToken)
    {
        foreach (var agent in agents)
        {
            var assignment = new CosmosAgentAssignment(agent, nodeId);
            await _container.UpsertItemAsync(assignment, new PartitionKey(DocumentTypes.SystemPartition),
                cancellationToken: cancellationToken);
        }
    }

    public async Task RemoveAssignmentAsync(Guid nodeId, Uri agentUri, CancellationToken cancellationToken)
    {
        try
        {
            await _container.DeleteItemAsync<dynamic>(
                CosmosAgentAssignment.ToId(agentUri), new PartitionKey(DocumentTypes.SystemPartition),
                cancellationToken: cancellationToken);
        }
        catch (CosmosException e) when (e.StatusCode == HttpStatusCode.NotFound)
        {
        }
    }

    public async Task AddAssignmentAsync(Guid nodeId, Uri agentUri, CancellationToken cancellationToken)
    {
        var assignment = new CosmosAgentAssignment(agentUri, nodeId);
        await _container.UpsertItemAsync(assignment, new PartitionKey(DocumentTypes.SystemPartition),
            cancellationToken: cancellationToken);
    }

    public async Task<WolverineNode?> LoadNodeAsync(Guid nodeId, CancellationToken cancellationToken)
    {
        try
        {
            var response = await _container.ReadItemAsync<CosmosWolverineNode>(
                $"node|{nodeId}", new PartitionKey(DocumentTypes.SystemPartition),
                cancellationToken: cancellationToken);

            var node = response.Resource.ToWolverineNode();

            var assignmentQuery =
                new QueryDefinition(
                        "SELECT * FROM c WHERE c.docType = @docType AND c.nodeId = @nodeId")
                    .WithParameter("@docType", DocumentTypes.AgentAssignment)
                    .WithParameter("@nodeId", nodeId.ToString());

            var assignments = new List<CosmosAgentAssignment>();
            using var iterator = _container.GetItemQueryIterator<CosmosAgentAssignment>(assignmentQuery,
                requestOptions: new QueryRequestOptions
                {
                    PartitionKey = new PartitionKey(DocumentTypes.SystemPartition)
                });

            while (iterator.HasMoreResults)
            {
                var resp = await iterator.ReadNextAsync(cancellationToken);
                assignments.AddRange(resp);
            }

            node.ActiveAgents.Clear();
            node.ActiveAgents.AddRange(assignments.OrderBy(x => x.Id).Select(x => new Uri(x.AgentUri)));

            return node;
        }
        catch (CosmosException e) when (e.StatusCode == HttpStatusCode.NotFound)
        {
            return null;
        }
    }

    public async Task MarkHealthCheckAsync(WolverineNode node, CancellationToken cancellationToken)
    {
        try
        {
            var response = await _container.ReadItemAsync<CosmosWolverineNode>(
                $"node|{node.NodeId}", new PartitionKey(DocumentTypes.SystemPartition),
                cancellationToken: cancellationToken);
            var doc = response.Resource;
            doc.LastHealthCheck = DateTimeOffset.UtcNow;
            await _container.ReplaceItemAsync(doc, doc.Id, new PartitionKey(DocumentTypes.SystemPartition),
                cancellationToken: cancellationToken);
        }
        catch (CosmosException e) when (e.StatusCode == HttpStatusCode.NotFound)
        {
            // Node doesn't exist, create it
            var doc = new CosmosWolverineNode(node) { LastHealthCheck = DateTimeOffset.UtcNow };
            await _container.UpsertItemAsync(doc, new PartitionKey(DocumentTypes.SystemPartition),
                cancellationToken: cancellationToken);
        }
    }

    public async Task OverwriteHealthCheckTimeAsync(Guid nodeId, DateTimeOffset lastHeartbeatTime)
    {
        try
        {
            var response = await _container.ReadItemAsync<CosmosWolverineNode>(
                $"node|{nodeId}", new PartitionKey(DocumentTypes.SystemPartition));
            var doc = response.Resource;
            doc.LastHealthCheck = lastHeartbeatTime;
            await _container.ReplaceItemAsync(doc, doc.Id, new PartitionKey(DocumentTypes.SystemPartition));
        }
        catch (CosmosException e) when (e.StatusCode == HttpStatusCode.NotFound)
        {
            // Node doesn't exist
        }
    }

    public async Task LogRecordsAsync(params NodeRecord[] records)
    {
        foreach (var record in records)
        {
            var doc = new CosmosNodeRecord(record);
            await _container.UpsertItemAsync(doc, new PartitionKey(DocumentTypes.SystemPartition));
        }
    }

    public async Task<IReadOnlyList<NodeRecord>> FetchRecentRecordsAsync(int count)
    {
        var queryText =
            "SELECT * FROM c WHERE c.docType = @docType ORDER BY c.timestamp DESC OFFSET 0 LIMIT @limit";
        var query = new QueryDefinition(queryText)
            .WithParameter("@docType", DocumentTypes.NodeRecord)
            .WithParameter("@limit", count);

        var results = new List<CosmosNodeRecord>();
        using var iterator = _container.GetItemQueryIterator<CosmosNodeRecord>(query,
            requestOptions: new QueryRequestOptions
            {
                PartitionKey = new PartitionKey(DocumentTypes.SystemPartition)
            });

        while (iterator.HasMoreResults)
        {
            var response = await iterator.ReadNextAsync();
            results.AddRange(response);
        }

        results.Reverse();
        return results.Select(x => x.ToNodeRecord()).ToList();
    }
}

// Helper document types for CosmosDB

public class CosmosWolverineNode
{
    public CosmosWolverineNode()
    {
    }

    public CosmosWolverineNode(WolverineNode node)
    {
        Id = $"node|{node.NodeId}";
        NodeId = node.NodeId.ToString();
        Description = node.Description;
        AssignedNodeNumber = node.AssignedNodeNumber;
        ControlUri = node.ControlUri?.ToString();
        LastHealthCheck = node.LastHealthCheck;
        Started = node.Started;
        Version = node.Version?.ToString();
        Capabilities = node.Capabilities.Select(x => x.ToString()).ToList();
    }

    [JsonProperty("id")] public string Id { get; set; } = string.Empty;

    [JsonProperty("docType")] public string DocType { get; set; } = DocumentTypes.Node;

    [JsonProperty("partitionKey")] public string PartitionKey { get; set; } = DocumentTypes.SystemPartition;

    [JsonProperty("nodeId")] public string NodeId { get; set; } = string.Empty;

    [JsonProperty("description")] public string? Description { get; set; }

    [JsonProperty("assignedNodeNumber")]
    public int AssignedNodeNumber { get; set; }

    [JsonProperty("controlUri")] public string? ControlUri { get; set; }

    [JsonProperty("lastHealthCheck")] public DateTimeOffset LastHealthCheck { get; set; }

    [JsonProperty("started")] public DateTimeOffset Started { get; set; }

    [JsonProperty("version")] public string? Version { get; set; }

    [JsonProperty("capabilities")] public List<string> Capabilities { get; set; } = new();

    public WolverineNode ToWolverineNode()
    {
        var node = new WolverineNode
        {
            NodeId = Guid.Parse(NodeId),
            Description = Description ?? Environment.MachineName,
            AssignedNodeNumber = AssignedNodeNumber,
            ControlUri = ControlUri != null ? new Uri(ControlUri) : null,
            LastHealthCheck = LastHealthCheck,
            Started = Started,
            Version = Version != null ? new Version(Version) : new Version(0, 0, 0, 0)
        };
        node.Capabilities.AddRange(Capabilities.Select(x => new Uri(x)));
        return node;
    }
}

public class CosmosAgentAssignment
{
    public static string ToId(Uri uri)
    {
        return "agent|" + uri.ToString().TrimEnd('/').Replace("//", "/").Replace(':', '_').Replace('/', '_');
    }

    public CosmosAgentAssignment()
    {
    }

    public CosmosAgentAssignment(Uri agentUri, Guid nodeId)
    {
        Id = ToId(agentUri);
        NodeId = nodeId.ToString();
        AgentUri = agentUri.ToString();
    }

    [JsonProperty("id")] public string Id { get; set; } = string.Empty;

    [JsonProperty("docType")] public string DocType { get; set; } = DocumentTypes.AgentAssignment;

    [JsonProperty("partitionKey")] public string PartitionKey { get; set; } = DocumentTypes.SystemPartition;

    [JsonProperty("nodeId")] public string NodeId { get; set; } = string.Empty;

    [JsonProperty("agentUri")] public string AgentUri { get; set; } = string.Empty;
}

public class CosmosNodeSequence
{
    [JsonProperty("id")] public string Id { get; set; } = $"{DocumentTypes.NodeSequence}|sequence";

    [JsonProperty("docType")] public string DocType { get; set; } = DocumentTypes.NodeSequence;

    [JsonProperty("partitionKey")] public string PartitionKey { get; set; } = DocumentTypes.SystemPartition;

    [JsonProperty("count")] public int Count { get; set; }
}

public class CosmosAgentRestriction
{
    [JsonProperty("id")] public string Id { get; set; } = string.Empty;

    [JsonProperty("docType")] public string DocType { get; set; } = DocumentTypes.AgentRestriction;

    [JsonProperty("partitionKey")] public string PartitionKey { get; set; } = DocumentTypes.SystemPartition;

    [JsonProperty("agentUri")] public string AgentUri { get; set; } = string.Empty;

    [JsonProperty("type")] public AgentRestrictionType Type { get; set; }

    [JsonProperty("nodeNumber")] public int NodeNumber { get; set; }
}

public class CosmosNodeRecord
{
    public CosmosNodeRecord()
    {
    }

    public CosmosNodeRecord(NodeRecord record)
    {
        Id = $"record|{Guid.NewGuid()}";
        NodeNumber = record.NodeNumber;
        RecordType = record.RecordType;
        Timestamp = record.Timestamp;
        Description = record.Description;
        ServiceName = record.ServiceName;
    }

    [JsonProperty("id")] public string Id { get; set; } = string.Empty;

    [JsonProperty("docType")] public string DocType { get; set; } = DocumentTypes.NodeRecord;

    [JsonProperty("partitionKey")] public string PartitionKey { get; set; } = DocumentTypes.SystemPartition;

    [JsonProperty("nodeNumber")] public int NodeNumber { get; set; }

    [JsonProperty("recordType")] public NodeRecordType RecordType { get; set; }

    [JsonProperty("timestamp")] public DateTimeOffset Timestamp { get; set; }

    [JsonProperty("description")] public string? Description { get; set; }

    [JsonProperty("serviceName")] public string? ServiceName { get; set; }

    public NodeRecord ToNodeRecord()
    {
        return new NodeRecord
        {
            NodeNumber = NodeNumber,
            RecordType = RecordType,
            Timestamp = Timestamp,
            Description = Description ?? string.Empty,
            ServiceName = ServiceName ?? string.Empty
        };
    }
}
