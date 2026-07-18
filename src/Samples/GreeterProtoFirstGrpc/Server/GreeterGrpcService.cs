using GreeterProtoFirstGrpc.Messages;
using Wolverine.Grpc;

namespace GreeterProtoFirstGrpc.Server;

/// <summary>
///     Proto-first Wolverine gRPC stub. The proto-generated <c>Greeter.GreeterBase</c>
///     supplies the gRPC contract; at startup, Wolverine code-generates a concrete
///     subclass of this stub that overrides every RPC — unary, server-streaming, and
///     client-streaming alike — and forwards it to the matching <see cref="IMessageBus"/>
///     operation. No bridging code is written by hand.
/// </summary>
[WolverineGrpcService]
public abstract class GreeterGrpcService : Greeter.GreeterBase;
