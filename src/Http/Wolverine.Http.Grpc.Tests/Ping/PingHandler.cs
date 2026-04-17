namespace Wolverine.Http.Grpc.Tests;

/// <summary>
/// Wolverine handler for PingRequest — returns a PongReply.
/// This is the "business logic" that the gRPC service delegates to.
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
/// Singleton counter to verify handler invocation in tests.
/// </summary>
public class PingTracker
{
    private int _count;
    public int Count => _count;
    public void Increment() => Interlocked.Increment(ref _count);
}
