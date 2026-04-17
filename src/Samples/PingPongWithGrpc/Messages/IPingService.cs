using System.ServiceModel;
using ProtoBuf;
using ProtoBuf.Grpc;

namespace PingPongWithGrpc.Messages;

/// <summary>
///     Code-first gRPC contract shared between <c>Pinger</c> (client) and <c>Ponger</c> (server).
///     The <c>[ServiceContract]</c> attribute tells protobuf-net.Grpc to expose this interface
///     as a gRPC service; both sides reference the same interface type via this Messages project.
/// </summary>
[ServiceContract]
public interface IPingService
{
    Task<PongReply> Ping(PingRequest request, CallContext context = default);
}

[ProtoContract]
public class PingRequest
{
    [ProtoMember(1)]
    public string Message { get; set; } = string.Empty;
}

[ProtoContract]
public class PongReply
{
    [ProtoMember(1)]
    public string Echo { get; set; } = string.Empty;

    [ProtoMember(2)]
    public int HandledCount { get; set; }
}
