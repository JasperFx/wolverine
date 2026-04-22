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
}
