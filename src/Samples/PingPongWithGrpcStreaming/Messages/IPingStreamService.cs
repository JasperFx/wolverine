using System.ServiceModel;
using ProtoBuf;
using ProtoBuf.Grpc;

namespace PingPongWithGrpcStreaming.Messages;

/// <summary>
///     Code-first gRPC contract exercising a server-streaming RPC. Returning
///     <see cref="IAsyncEnumerable{T}"/> is how protobuf-net.Grpc recognises the method
///     as server-streaming (analogous to <c>stream PongReply</c> in a .proto).
/// </summary>
[ServiceContract]
public interface IPingStreamService
{
    IAsyncEnumerable<PongReply> PingStream(PingStreamRequest request, CallContext context = default);
}

[ProtoContract]
public class PingStreamRequest
{
    [ProtoMember(1)]
    public string Message { get; set; } = string.Empty;

    [ProtoMember(2)]
    public int Count { get; set; }
}

[ProtoContract]
public class PongReply
{
    [ProtoMember(1)]
    public string Echo { get; set; } = string.Empty;

    [ProtoMember(2)]
    public int HandledCount { get; set; }
}
