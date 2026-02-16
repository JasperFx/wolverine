namespace Wolverine.CosmosDb.Internals;

public static class DocumentTypes
{
    public const string Incoming = "incoming";
    public const string Outgoing = "outgoing";
    public const string DeadLetter = "deadletter";
    public const string Node = "node";
    public const string AgentAssignment = "agent-assignment";
    public const string Lock = "lock";
    public const string NodeRecord = "node-record";
    public const string AgentRestriction = "agent-restriction";
    public const string NodeSequence = "node-sequence";

    public const string ContainerName = "wolverine";
    public const string PartitionKeyPath = "/partitionKey";

    public const string SystemPartition = "system";
    public const string DeadLetterPartition = "deadletter";
}
