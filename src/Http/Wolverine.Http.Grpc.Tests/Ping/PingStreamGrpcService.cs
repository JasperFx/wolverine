using ProtoBuf.Grpc;

namespace Wolverine.Http.Grpc.Tests;

/// <summary>
/// Code-first gRPC service that forwards a server-streaming call to Wolverine's
/// <see cref="IMessageBus.StreamAsync{T}"/>. The "GrpcService" suffix means
/// <c>MapWolverineGrpcServices()</c> will discover this type via convention.
/// </summary>
public class PingStreamGrpcService : WolverineGrpcServiceBase, IPingStreamService
{
    public PingStreamGrpcService(IMessageBus bus) : base(bus)
    {
    }

    public IAsyncEnumerable<PongReply> PingStream(PingStreamRequest request, CallContext context = default)
        => Bus.StreamAsync<PongReply>(request, context.CancellationToken);
}
