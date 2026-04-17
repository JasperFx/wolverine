using GreeterProtoFirstGrpc.Messages;
using Wolverine.Http.Grpc;

namespace GreeterProtoFirstGrpc.Server;

/// <summary>
///     Proto-first Wolverine gRPC stub. The proto-generated <c>Greeter.GreeterBase</c>
///     supplies the gRPC contract; at startup, Wolverine code-generates a concrete
///     subclass of this stub that overrides every unary RPC and forwards it to
///     <see cref="IMessageBus.InvokeAsync{T}"/> — no bridging code is written by hand.
/// </summary>
[WolverineGrpcService]
public abstract class GreeterGrpcService : Greeter.GreeterBase;
