namespace Wolverine.RDBMS;

public class DatabaseConstants
{
    public const string Id = "id";
    public const string OwnerId = "owner_id";
    public const string Destination = "destination";
    public const string DeliverBy = "deliver_by";
    public const string Body = "body";
    public const string Version = "version";
    public const string Status = "status";

    public const string ExecutionTime = "execution_time";
    public const string Attempts = "attempts";
    public const string Source = "source";
    public const string MessageType = "message_type";

    public const string ExceptionType = "exception_type";
    public const string ExceptionMessage = "exception_message";
    public const string Replayable = "replayable";

    public const string OutgoingTable = "wolverine_outgoing_envelopes";
    public const string IncomingTable = "wolverine_incoming_envelopes";
    public const string DeadLetterTable = "wolverine_dead_letters";

    public const string ControlQueueTableName = "wolverine_control_queue";

    public const string NodeTableName = "wolverine_nodes";
    public const string NodeAssignmentsTableName = "wolverine_node_assignments";

    public const string ReceivedAt = "received_at"; // add to all
    public const string SentAt = "sent_at"; // add to all

    public const string KeepUntil = "keep_until";
    public const string NodeRecordTableName = "wolverine_node_records";

    public const string Expires = "expires";

    public static readonly string IncomingFields =
        $"{Body}, {Id}, {Status}, {OwnerId}, {ExecutionTime}, {Attempts}, {MessageType}, {ReceivedAt}";

    public static readonly string OutgoingFields =
        $"{Body}, {Id}, {OwnerId}, {Destination}, {DeliverBy}, {Attempts}, {MessageType}";

    public static readonly string DeadLetterFields =
        $"{Id}, {ExecutionTime}, {Body}, {MessageType}, {ReceivedAt}, {Source}, {ExceptionType}, {ExceptionMessage}, {SentAt}, {Replayable}";

    public static readonly string NodeNumber = "node_number";
    public static readonly string NodeId = "node_id";
    public static readonly string Description = "description";
    public static readonly string Uri = "uri";
    public static readonly string Started = "started";
    public static readonly string HealthCheck = "health_check";
    public static readonly string Capabilities = "capabilities";
    public static readonly string NodeColumns = $"{Id}, {NodeNumber}, {Description}, {Uri}, {Started}, {HealthCheck}, {Version}, {Capabilities}";
}