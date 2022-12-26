using Wolverine.Util;

namespace Wolverine.Transports;

public static class TransportConstants
{
    internal const string SerializedEnvelope = "binary/envelope";
    internal const string ScheduledEnvelope = "scheduled-envelope";

    public const string Durable = "durable";

    public const string Default = "default";
    public const string Replies = "replies";

    public const string WolverineTransport = "WolverineTransport";
    internal static readonly int ScheduledJobLockId = "scheduled-jobs".GetDeterministicHashCode();
    internal static readonly int IncomingMessageLockId = "recover-incoming-messages".GetDeterministicHashCode();
    internal static readonly int OutgoingMessageLockId = "recover-outgoing-messages".GetDeterministicHashCode();
    internal static readonly int ReassignmentLockId = "wolverine-reassign-envelopes".GetDeterministicHashCode();
    public static readonly string Local = "local";

    public static readonly Uri RetryUri = "local://retries".ToUri();
    public static readonly Uri RepliesUri = "local://replies".ToUri();
    public static readonly Uri ScheduledUri = "local://delayed".ToUri();

    public static readonly Uri DurableLocalUri = "local://durable".ToUri();
    public static readonly Uri LocalUri = "local://".ToUri();

    public static readonly int AnyNode = 0;
}