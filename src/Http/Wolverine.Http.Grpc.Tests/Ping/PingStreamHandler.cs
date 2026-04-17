using System.Runtime.CompilerServices;

namespace Wolverine.Http.Grpc.Tests;

/// <summary>
/// Wolverine handler that produces a stream of <see cref="PongReply"/> messages in response to
/// a single <see cref="PingStreamRequest"/>. Wolverine's M2 streaming handler support plus
/// <see cref="IMessageBus.StreamAsync{T}"/> are what lets the code-first gRPC service simply
/// return the bus stream.
/// </summary>
public static class PingStreamHandler
{
    public static async IAsyncEnumerable<PongReply> Handle(
        PingStreamRequest request,
        PingTracker tracker,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        for (var i = 0; i < request.Count; i++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            tracker.Increment();
            yield return new PongReply
            {
                Echo = $"{request.Message}:{i}",
                HandledCount = tracker.Count
            };
            await Task.Yield();
        }
    }
}
