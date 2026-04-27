using Grpc.Core;
using ProtoBuf.Grpc;
using Wolverine.Grpc.Tests.GrpcMiddlewareScoping;

namespace Wolverine.Grpc.Tests.HandWrittenChain;

/// <summary>
///     Hand-written code-first gRPC service under test.  The <c>GrpcService</c> suffix
///     triggers Wolverine's hand-written-chain discovery; no <c>[WolverineGrpcService]</c>
///     attribute is needed.
///
///     The <c>Validate</c> method exercises the <c>Status?</c> short-circuit hook that
///     Wolverine weaves into the generated delegation wrapper.
/// </summary>
public class HandWrittenTestGrpcService : IHandWrittenTestService
{
    public const string ValidateMarker = "HandWrittenTest.Validate";
    public const string BeforeMarker = "HandWrittenTest.Before";

    private readonly MiddlewareInvocationSink _sink;

    public HandWrittenTestGrpcService(MiddlewareInvocationSink sink)
    {
        _sink = sink;
    }

    // Validate is the short-circuit hook: returning non-null aborts the call.
    public static Status? Validate(HandWrittenTestRequest request)
    {
        if (string.IsNullOrWhiteSpace(request.Text))
            return new Status(StatusCode.InvalidArgument, "Text is required");
        return null;
    }

    public Task<HandWrittenTestReply> Echo(HandWrittenTestRequest request, CallContext context = default)
        => Task.FromResult(new HandWrittenTestReply { Echo = request.Text });

    public async IAsyncEnumerable<HandWrittenTestReply> EchoStream(HandWrittenTestStreamRequest request, CallContext context = default)
    {
        for (var i = 0; i < request.Count; i++)
        {
            await Task.Yield();
            yield return new HandWrittenTestReply { Echo = $"{request.Text}:{i}" };
        }
    }

    public async IAsyncEnumerable<HandWrittenTestReply> EchoBidi(IAsyncEnumerable<HandWrittenTestRequest> requests, CallContext context = default)
    {
        await foreach (var req in requests)
            yield return new HandWrittenTestReply { Echo = req.Text };
    }
}
