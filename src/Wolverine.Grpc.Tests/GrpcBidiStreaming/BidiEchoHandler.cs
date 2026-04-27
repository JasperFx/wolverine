using System.Runtime.CompilerServices;
using Wolverine.Grpc.Tests.GrpcBidiStreaming.Generated;

namespace Wolverine.Grpc.Tests.GrpcBidiStreaming;

public static class BidiEchoHandler
{
    public static async IAsyncEnumerable<EchoReply> Handle(
        EchoRequest request,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        for (var i = 0; i < request.RepeatCount; i++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            yield return new EchoReply { Text = request.Text };
        }

        await Task.Yield();
    }
}
