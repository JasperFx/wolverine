using System.Runtime.CompilerServices;
using GreeterCodeFirstGrpc.Messages;

namespace GreeterCodeFirstGrpc.Server;

/// <summary>
///     Wolverine handlers for the code-first Greeter service. These are plain static
///     methods — no base class, no interface implementation, no service registration.
///     Wolverine discovers them by convention and the generated
///     <c>GreeterCodeFirstServiceGrpcHandler</c> forwards each RPC here via
///     <c>IMessageBus.InvokeAsync</c> / <c>IMessageBus.StreamAsync</c>.
/// </summary>
public static class GreeterHandler
{
    public static GreetReply Handle(GreetRequest request)
        => new() { Message = $"Hello, {request.Name}!" };

    public static async IAsyncEnumerable<GreetReply> Handle(
        StreamGreetingsRequest request,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        for (var i = 1; i <= request.Count; i++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            yield return new GreetReply { Message = $"Hello, {request.Name} [{i} of {request.Count}]" };
            await Task.Yield();
        }
    }

    // Client streaming: the generated implementation receives the RPC's inbound stream as
    // IAsyncEnumerable<GreetRequest> and forwards it via IMessageBus.StreamAsync, so the
    // handler folds N requests into one reply.
    public static async Task<GreetingSummary> Handle(
        IAsyncEnumerable<GreetRequest> requests,
        CancellationToken cancellationToken)
    {
        var names = new List<string>();
        await foreach (var request in requests.WithCancellation(cancellationToken))
        {
            names.Add(request.Name);
        }

        return new GreetingSummary
        {
            Count = names.Count,
            Message = names.Count == 0 ? "Hello, nobody" : $"Hello, {string.Join(" & ", names)}"
        };
    }
}
