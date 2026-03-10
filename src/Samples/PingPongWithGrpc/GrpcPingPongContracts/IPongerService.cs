using ProtoBuf;
using ProtoBuf.Grpc;
using System.ServiceModel;

namespace GrpcPingPongContracts;

/// <summary>
/// A ping message sent from the Pinger service to the Ponger service via gRPC.
/// </summary>
[ProtoContract]
public class PingMessage
{
    [ProtoMember(1)]
    public int Number { get; set; }

    [ProtoMember(2)]
    public string Message { get; set; } = string.Empty;
}

/// <summary>
/// A pong reply sent back from the Ponger service in response to a <see cref="PingMessage"/>.
/// </summary>
[ProtoContract]
public class PongMessage
{
    [ProtoMember(1)]
    public int Number { get; set; }

    [ProtoMember(2)]
    public string Message { get; set; } = string.Empty;
}

/// <summary>
/// The gRPC service contract for the Ponger service.
/// Both the Pinger (client) and Ponger (server) reference this shared interface.
/// </summary>
[ServiceContract]
public interface IPongerService
{
    /// <summary>
    /// Sends a Ping to the Ponger and receives a Pong in return.
    /// </summary>
    [OperationContract]
    Task<PongMessage> SendPingAsync(PingMessage request, CallContext context = default);
}
