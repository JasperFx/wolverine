namespace Wolverine.RavenDb.Internals;

public class AgentAssignment
{
    public static string ToId(Uri uri)
    {
        return uri.ToString().TrimEnd('/').Replace("//", "/").Replace(':', '_').Replace('/', '_');
    }
    
    public AgentAssignment()
    {
        
    }

    public AgentAssignment(Uri agentUri, Guid nodeId)
    {
        Id = ToId(agentUri);
        NodeId = nodeId; 
        AgentUri = agentUri;
    }

    public Uri AgentUri { get; set; } = null!;

    public string Id { get; set; } = null!;

    public Guid NodeId { get; set; }
}