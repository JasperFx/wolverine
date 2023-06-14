using JasperFx.Core;

namespace Wolverine.Transports;

public static class TransportConstants
{
    internal const string SerializedEnvelope = "binary/envelope";
    internal const string ScheduledEnvelope = "scheduled-envelope";

    public const string Durable = "durable";

    public const string Default = "default";
    public const string Replies = "replies";
    public const string System = "system";

    public const string WolverineTransport = "WolverineTransport";
    public const string Agents = "agents";

    public const string ProtocolVersion = "wolverine-protocol-version";
    public static readonly Uri SystemQueueUri = "local://system".ToUri();
    public static readonly string Local = "local";

    public static readonly Uri RepliesUri = "local://replies".ToUri();
    public static readonly string Scheduled = "scheduled";

    public static readonly Uri DurableLocalUri = "local://durable".ToUri();
    public static readonly Uri LocalUri = "local://".ToUri();

    public static readonly int AnyNode = 0;
}