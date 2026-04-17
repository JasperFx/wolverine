using System.Runtime.CompilerServices;
using PingPongWithGrpcStreaming.Messages;

namespace PingPongWithGrpcStreaming.Ponger;

/// <summary>
///     Wolverine streaming handler — yields <see cref="PongReply"/> messages one at a time.
///     The <c>IAsyncEnumerable&lt;T&gt;</c> return shape is Wolverine's M2 streaming handler
///     contract; the gRPC service layer simply forwards via <c>IMessageBus.StreamAsync&lt;T&gt;</c>.
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

/// <summary>
///     Singleton counter so the sample can illustrate that every yielded item corresponds to
///     a handler pass (rather than being batched).
/// </summary>
public class PingTracker
{
    private int _count;
    public int Count => _count;
    public void Increment() => Interlocked.Increment(ref _count);
}
