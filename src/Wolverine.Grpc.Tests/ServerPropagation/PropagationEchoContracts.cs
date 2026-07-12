using ProtoBuf;
using ProtoBuf.Grpc;
using System.ServiceModel;

namespace Wolverine.Grpc.Tests.ServerPropagation;

/// <summary>
///     Code-first gRPC contract whose server implementation forwards to the Wolverine bus. The
///     invoked handler reads the ambient <see cref="IMessageContext"/> and echoes back whatever
///     <see cref="WolverineGrpcServicePropagationInterceptor"/> put there from inbound headers —
///     used to prove the server-side half of a Wolverine-to-Wolverine propagation hop actually
///     lands in the handler, not just on the wire.
/// </summary>
[ServiceContract]
public interface IPropagationEchoService
{
    Task<PropagationEchoReply> Echo(PropagationEchoRequest request, CallContext context = default);
}

[ProtoContract]
public class PropagationEchoRequest
{
}

[ProtoContract]
public class PropagationEchoReply
{
    [ProtoMember(1)]
    public string? CorrelationId { get; set; }

    [ProtoMember(2)]
    public string? TenantId { get; set; }
}
