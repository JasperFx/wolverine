namespace Wolverine;

public class MetricsConstants
{
    public const string MessagesSent = "wolverine-messages-sent";
    public const string ExecutionTime = "wolverine-execution-time";
    public const string MessagesSucceeded = "wolverine-messages-succeeded";
    public const string DeadLetterQueue = "wolverine-dead-letter-queue";
    public const string EffectiveMessageTime = "wolverine-effective-time";

    public const string Milliseconds = "Milliseconds";
    public const string Messages = "Messages";

    public const string InboxCount = "wolverine-inbox-count";
    public const string OutboxCount = "wolverine-outbox-count";
    public const string ScheduledCount = "wolverine-scheduled-count";
}