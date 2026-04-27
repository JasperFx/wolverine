using ProtoBuf;
using ProtoBuf.Grpc;
using System.ServiceModel;

namespace Wolverine.Grpc.Tests.HandWrittenChain;

/// <summary>
///     Test-only code-first gRPC contract for hand-written chain tests.
///     Deliberately does NOT carry <c>[WolverineGrpcService]</c> — the concrete class
///     (<see cref="HandWrittenTestGrpcService"/>) is what Wolverine discovers as a hand-written
///     service (via the <c>GrpcService</c> suffix convention).
/// </summary>
[ServiceContract]
public interface IHandWrittenTestService
{
    Task<HandWrittenTestReply> Echo(HandWrittenTestRequest request, CallContext context = default);

    IAsyncEnumerable<HandWrittenTestReply> EchoStream(HandWrittenTestStreamRequest request, CallContext context = default);

    IAsyncEnumerable<HandWrittenTestReply> EchoBidi(IAsyncEnumerable<HandWrittenTestRequest> requests, CallContext context = default);
}

[ProtoContract]
public class HandWrittenTestRequest
{
    [ProtoMember(1)] public string Text { get; set; } = string.Empty;
}

[ProtoContract]
public class HandWrittenTestStreamRequest
{
    [ProtoMember(1)] public string Text { get; set; } = string.Empty;
    [ProtoMember(2)] public int Count { get; set; }
}

[ProtoContract]
public class HandWrittenTestReply
{
    [ProtoMember(1)] public string Echo { get; set; } = string.Empty;
}
