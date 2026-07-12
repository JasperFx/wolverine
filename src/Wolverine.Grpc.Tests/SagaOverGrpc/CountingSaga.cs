namespace Wolverine.Grpc.Tests.SagaOverGrpc;

/// <summary>
///     A <em>header-identified</em> saga: <see cref="StartCountingRequest"/> carries no id member,
///     so Wolverine resolves the saga id from the inbound envelope's <c>saga-id</c> header. Over a
///     gRPC service hop that header is never populated — neither the client nor server propagation
///     interceptor carries <c>saga-id</c>, and <c>Executor.InvokeAsync&lt;T&gt;</c> does not seed it
///     onto the invoked envelope — so this saga reproduces the <c>IndeterminateSagaStateIdException</c>
///     gap (GH-3385). <c>StartOrHandle</c> takes the "maybe-existing" code path, which pulls the id
///     from the envelope on the very first call, so a single RPC is enough to reproduce.
/// </summary>
public class CountingSaga : Saga
{
    public string Id { get; set; } = string.Empty;
    public int Count { get; set; }

    public StartCountingReply StartOrHandle(StartCountingRequest request)
    {
        Count++;
        return new StartCountingReply { SagaId = Id };
    }
}
