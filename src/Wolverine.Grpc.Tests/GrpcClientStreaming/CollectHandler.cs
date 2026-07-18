using Wolverine.Grpc.Tests.GrpcClientStreaming.Generated;

namespace Wolverine.Grpc.Tests.GrpcClientStreaming;

/// <summary>
///     Wolverine handler for the client-streaming shape: receives the whole inbound RPC
///     stream as <see cref="IAsyncEnumerable{T}"/> and folds it into a single reply.
/// </summary>
public static class CollectHandler
{
    public static async Task<SumReply> Handle(IAsyncEnumerable<NumberRequest> numbers,
        CancellationToken cancellationToken)
    {
        var total = 0;
        var count = 0;
        await foreach (var number in numbers.WithCancellation(cancellationToken))
        {
            total += number.Value;
            count++;
        }

        return new SumReply { Total = total, Count = count };
    }
}
