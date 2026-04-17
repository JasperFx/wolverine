using PingPongWithGrpc.Messages;

namespace PingPongWithGrpc.Ponger;

/// <summary>
///     Wolverine handler for <see cref="PingRequest"/>. The gRPC service forwards to
///     the bus and the bus invokes this handler — the handler itself has no gRPC coupling.
/// </summary>
public static class PingHandler
{
    public static PongReply Handle(PingRequest request, PingTracker tracker)
    {
        tracker.Increment();
        return new PongReply
        {
            Echo = request.Message,
            HandledCount = tracker.Count
        };
    }
}

/// <summary>
///     Singleton counter used by the sample to show that every gRPC call lands in the handler.
/// </summary>
public class PingTracker
{
    private int _count;
    public int Count => _count;
    public void Increment() => Interlocked.Increment(ref _count);
}
