using System.Collections.Concurrent;

namespace Wolverine.Grpc.Tests.GrpcMiddlewareScoping;

/// <summary>
///     Thread-safe append-only ledger of named events that the M15 integration tests use to
///     assert middleware ordering and scope filtering. Each captured entry records the marker
///     name supplied by a test fixture (typically a stub-class method name like
///     <c>"BeforeGrpc"</c> or a handler name like <c>"Handler"</c>) so the test can later
///     assert what fired and in what order without coupling to clock timing.
/// </summary>
public sealed class MiddlewareInvocationSink
{
    private readonly ConcurrentQueue<string> _events = new();

    public void Record(string marker) => _events.Enqueue(marker);

    public IReadOnlyList<string> Events => _events.ToArray();

    public void Clear()
    {
        while (_events.TryDequeue(out _))
        {
        }
    }

    public bool Contains(string marker) => _events.Contains(marker);

    public int CountOf(string marker) => _events.Count(e => e == marker);
}
