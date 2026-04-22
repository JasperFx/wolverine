using System.ServiceModel;
using ProtoBuf;
using ProtoBuf.Grpc;
using Wolverine.Grpc;

namespace Wolverine.Grpc.Tests.CodeFirstCodegen;

/// <summary>
///     Test-only code-first gRPC contract. Annotated with both
///     <c>[ServiceContract]</c> (required by protobuf-net.Grpc) and
///     <c>[WolverineGrpcService]</c> (tells Wolverine to generate the implementation).
///     No concrete class is written — Wolverine generates one at startup.
/// </summary>
[ServiceContract]
[WolverineGrpcService]
public interface ICodeFirstTestService
{
    Task<CodeFirstReply> Echo(CodeFirstRequest request, CallContext context = default);
    IAsyncEnumerable<CodeFirstReply> EchoStream(CodeFirstStreamRequest request, CallContext context = default);
}

[ProtoContract]
public class CodeFirstRequest
{
    [ProtoMember(1)] public string Text { get; set; } = string.Empty;
}

[ProtoContract]
public class CodeFirstStreamRequest
{
    [ProtoMember(1)] public string Text { get; set; } = string.Empty;
    [ProtoMember(2)] public int Count { get; set; }
}

[ProtoContract]
public class CodeFirstReply
{
    [ProtoMember(1)] public string Echo { get; set; } = string.Empty;
}
