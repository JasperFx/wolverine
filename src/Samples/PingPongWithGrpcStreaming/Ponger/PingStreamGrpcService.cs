using PingPongWithGrpcStreaming.Messages;
using ProtoBuf.Grpc;
using Wolverine;
using Wolverine.Grpc;

namespace PingPongWithGrpcStreaming.Ponger;

/// <summary>
///     Code-first gRPC service that forwards a server-streaming call to Wolverine's
///     <see cref="IMessageBus.StreamAsync{T}"/>. protobuf-net.Grpc recognises the
///     <see cref="IAsyncEnumerable{T}"/> return type as server-streaming and wires the
///     transport accordingly.
/// </summary>
public class PingStreamGrpcService : WolverineGrpcServiceBase, IPingStreamService
{
    public PingStreamGrpcService(IMessageBus bus) : base(bus)
    {
    }

    public IAsyncEnumerable<PongReply> PingStream(PingStreamRequest request, CallContext context = default)
        => Bus.StreamAsync<PongReply>(request, context.CancellationToken);
}
