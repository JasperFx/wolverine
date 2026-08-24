namespace Wolverine;

public class MetricsConstants
{
    public const string MessagesSent = "wolverine-messages-sent";
    public const string ExecutionTime = "wolverine-execution-time";
    public const string MessagesSucceeded = "wolverine-messages-succeeded";
    public const string DeadLetterQueue = "wolverine-dead-letter-queue";
    public const string FaultPublishFailures = "wolverine-fault-publish-failures";
    public const string EffectiveMessageTime = "wolverine-effective-time";

    public const string Milliseconds = "Milliseconds";
    public const string Messages = "Messages";
    public const string Connections = "Connections";

    public const string InboxCount = "wolverine-inbox-count";
    public const string OutboxCount = "wolverine-outbox-count";
    public const string ScheduledCount = "wolverine-scheduled-count";

    // Connection budget, tagged by ServerKey. Server-scoped rather than database-scoped: the
    // connections are a resource of the server, shared by every database on it. See #3397.
    public const string DatabaseConnectionCount = "wolverine-database-connection-count";
    public const string DatabaseConnectionBudget = "wolverine-database-connection-budget";

    // GH-4048. Broker lease renewal for envelopes queued but not yet settled under EndpointMode.NativeAck.
    // "lost" is split by LeaseLossStageKey because the two halves mean different things: "not-started" is
    // duplication that was PREVENTED (the queued copy is dropped without being completed or deferred),
    // "executing" is duplication that was REALIZED and can only be reported. A rising "executing" count is
    // the operator's signal that lane depth has outgrown the configured lease.
    public const string LeasesRenewed = "wolverine-leases-renewed";
    public const string LeasesLost = "wolverine-leases-lost";
    public const string LeaseCeilingReached = "wolverine-lease-ceiling-reached";
    public const string LeaseLossStageKey = "lease.loss.stage";

    public const string MessageTypeKey = "message.type";
    public const string MessageDestinationKey = "message.destination";
    public const string TenantIdKey = "tenant.id";
    public const string MessagesReceived = "wolverine-messages-received";
    public const string MessagesFailed = "wolverine-execution-failure";
    public const string ExceptionType = "exception.type";
    public const string SourceKey = "source";
    public const string DatabaseKey = "database";
    public const string ServerKey = "server";
}