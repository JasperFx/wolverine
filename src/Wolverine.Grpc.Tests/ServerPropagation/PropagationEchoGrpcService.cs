using ProtoBuf.Grpc;
using Wolverine.Grpc;

namespace Wolverine.Grpc.Tests.ServerPropagation;

/// <summary>
///     Code-first gRPC service that delegates to the Wolverine bus. The "GrpcService" suffix lets
///     <c>MapWolverineGrpcServices()</c> discover and map this type automatically.
/// </summary>
public class PropagationEchoGrpcService : WolverineGrpcServiceBase, IPropagationEchoService
{
    public PropagationEchoGrpcService(IMessageBus bus) : base(bus)
    {
    }

    public Task<PropagationEchoReply> Echo(PropagationEchoRequest request, CallContext context = default)
        => Bus.InvokeAsync<PropagationEchoReply>(request, context.CancellationToken);
}

/// <summary>
///     Wolverine handler for <see cref="PropagationEchoRequest"/>. Reads whatever
///     <see cref="WolverineGrpcServicePropagationInterceptor"/> already stamped onto the ambient
///     <see cref="IMessageContext"/> before this handler ran.
/// </summary>
public static class PropagationEchoHandler
{
    public static PropagationEchoReply Handle(PropagationEchoRequest request, IMessageContext context)
    {
        return new PropagationEchoReply
        {
            CorrelationId = context.CorrelationId,
            TenantId = context.TenantId
        };
    }
}
