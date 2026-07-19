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
    Task<CodeFirstSumReply> SumStream(IAsyncEnumerable<CodeFirstNumber> numbers, CallContext context = default);
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

[ProtoContract]
public class CodeFirstNumber
{
    [ProtoMember(1)] public int Value { get; set; }
}

[ProtoContract]
public class CodeFirstSumReply
{
    [ProtoMember(1)] public int Total { get; set; }
    [ProtoMember(2)] public int Count { get; set; }
}

/// <summary>
///     Code-first client-streaming contract whose element type has NO Wolverine handler.
///     Generation must still succeed (the classifier and codegen don't require a handler);
///     calling the RPC surfaces the bus's <see cref="NotSupportedException"/> as
///     <c>Unimplemented</c> through the default exception mapping.
/// </summary>
[ServiceContract]
[WolverineGrpcService]
public interface ICodeFirstOrphanStreamService
{
    Task<CodeFirstSumReply> Collect(IAsyncEnumerable<CodeFirstOrphanNumber> numbers, CallContext context = default);
}

[ProtoContract]
public class CodeFirstOrphanNumber
{
    [ProtoMember(1)] public int Value { get; set; }
}

/// <summary>
///     Code-first service contract for the validate short-circuit test.
///     The static <c>Validate</c> method lives on <see cref="SubmitHandler"/> — the Wolverine
///     handler for <see cref="CodeFirstValidateRequest"/> — following the same idiom as the
///     rest of the framework: middleware hooks belong on the handler class.
/// </summary>
[ServiceContract]
[WolverineGrpcService]
public interface ICodeFirstValidatedService
{
    Task<CodeFirstReply> Submit(CodeFirstValidateRequest request, CallContext context = default);
}

[ProtoContract]
public class CodeFirstValidateRequest
{
    [ProtoMember(1)] public string Text { get; set; } = string.Empty;
}
