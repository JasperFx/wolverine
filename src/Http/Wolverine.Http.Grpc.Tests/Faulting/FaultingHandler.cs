using System.Runtime.CompilerServices;

namespace Wolverine.Http.Grpc.Tests;

/// <summary>
///     Wolverine handlers that deliberately throw so the gRPC exception interceptor
///     has something to translate. The specific exception type is selected by
///     <see cref="FaultCodeFirstRequest.Kind"/> via <see cref="FaultExceptions"/>.
/// </summary>
public static class FaultingHandler
{
    public static FaultCodeFirstReply Handle(FaultCodeFirstRequest request)
        => throw FaultExceptions.Throw(request.Kind);

    public static async IAsyncEnumerable<FaultCodeFirstReply> Handle(
        FaultStreamCodeFirstRequest request,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        // Yield one element so the client sees some progress before the fault —
        // this verifies mapping still works once the stream has started.
        yield return new FaultCodeFirstReply { Message = "about-to-fail" };
        await Task.Yield();
        throw FaultExceptions.Throw(request.Kind);
    }
}
