using System.Runtime.CompilerServices;
using Grpc.Core;

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

/// <summary>
///     Wolverine handler for the code-first client-streaming shape: receives the whole inbound
///     RPC stream as <see cref="IAsyncEnumerable{T}"/> and folds it into a single reply.
///     A <see cref="PoisonValue"/> item makes the handler throw mid-drain so tests can pin the
///     fault path (InvalidOperationException → FailedPrecondition via the default mapping).
/// </summary>
public static class SumStreamHandler
{
    public const int PoisonValue = -666;

    public static async Task<CodeFirstSumReply> Handle(IAsyncEnumerable<CodeFirstNumber> numbers,
        CancellationToken cancellationToken)
    {
        var total = 0;
        var count = 0;
        await foreach (var number in numbers.WithCancellation(cancellationToken))
        {
            if (number.Value == PoisonValue)
            {
                throw new InvalidOperationException("poison number encountered mid-drain");
            }

            total += number.Value;
            count++;
        }

        return new CodeFirstSumReply { Total = total, Count = count };
    }
}

public static class SubmitHandler
{
    public static Status? Validate(CodeFirstValidateRequest request)
        => request.Text == "bad" ? new Status(StatusCode.InvalidArgument, "bad input") : null;

    public static CodeFirstReply Handle(CodeFirstValidateRequest request)
        => new() { Echo = request.Text };
}
