namespace Wolverine.Http.Grpc.Tests.ProtoFirst;

/// <summary>
///     Proto-first Wolverine gRPC service stub. The proto-generated <c>Greeter.GreeterBase</c>
///     supplies the gRPC contract; Wolverine code-generates a concrete subclass of this stub
///     that overrides every unary RPC and forwards it to <see cref="IMessageBus.InvokeAsync{T}"/>.
///     No bridging code is written by hand.
/// </summary>
[WolverineGrpcService]
public abstract class GreeterGrpcService : Greeter.GreeterBase;
