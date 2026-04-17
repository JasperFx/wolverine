using ProtoBuf;
using ProtoBuf.Grpc;
using System.ServiceModel;

namespace Wolverine.Http.Grpc.Tests;

/// <summary>
///     Code-first contract exercising the <see cref="WolverineGrpcExceptionInterceptor"/> + mapper
///     pipeline end-to-end. Unary and server-streaming shapes both throw from the Wolverine handler
///     and the client should see an <see cref="Grpc.Core.RpcException"/> with the mapped StatusCode.
/// </summary>
[ServiceContract]
public interface IFaultingService
{
    Task<FaultCodeFirstReply> Throw(FaultCodeFirstRequest request, CallContext context = default);

    IAsyncEnumerable<FaultCodeFirstReply> ThrowStream(FaultStreamCodeFirstRequest request, CallContext context = default);
}

[ProtoContract]
public class FaultCodeFirstRequest
{
    [ProtoMember(1)]
    public string Kind { get; set; } = string.Empty;
}

[ProtoContract]
public class FaultStreamCodeFirstRequest
{
    [ProtoMember(1)]
    public string Kind { get; set; } = string.Empty;
}

[ProtoContract]
public class FaultCodeFirstReply
{
    [ProtoMember(1)]
    public string Message { get; set; } = string.Empty;
}
