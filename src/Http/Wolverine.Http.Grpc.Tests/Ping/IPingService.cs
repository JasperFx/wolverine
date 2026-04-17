using ProtoBuf.Grpc;
using ProtoBuf;
using System.ServiceModel;

namespace Wolverine.Http.Grpc.Tests;

/// <summary>
/// Code-first gRPC service contract for Ping/Pong round-trip tests.
/// The [ServiceContract] attribute tells protobuf-net.Grpc to expose this
/// interface as a gRPC service.
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
