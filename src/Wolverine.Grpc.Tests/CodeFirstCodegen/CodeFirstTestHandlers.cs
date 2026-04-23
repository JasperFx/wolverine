using System.Runtime.CompilerServices;

namespace Wolverine.Grpc.Tests.CodeFirstCodegen;

public static class EchoHandler
{
    public static CodeFirstReply Handle(CodeFirstRequest request)
        => new() { Echo = request.Text };
}

public static class EchoStreamHandler
{
    public static async IAsyncEnumerable<CodeFirstReply> Handle(
        CodeFirstStreamRequest request,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        for (var i = 0; i < request.Count; i++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            yield return new CodeFirstReply { Echo = $"{request.Text}:{i}" };
            await Task.Yield();
        }
    }
}

public static class SubmitHandler
{
    public static CodeFirstReply Handle(CodeFirstValidateRequest request)
        => new() { Echo = request.Text };
}
