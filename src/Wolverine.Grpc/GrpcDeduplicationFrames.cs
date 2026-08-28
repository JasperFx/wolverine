using Grpc.Core;
using JasperFx.CodeGeneration;
using JasperFx.CodeGeneration.Frames;
using JasperFx.CodeGeneration.Model;
using JasperFx.Core.Reflection;
using Wolverine.Configuration;
using Wolverine.Persistence;

namespace Wolverine.Grpc;

/// <summary>
/// GH-4180. The gRPC answer to a failed logical deduplication check: an <see cref="RpcException" />
/// carrying the canonical status code for the condition.
///
/// <para>
/// A duplicate is <see cref="StatusCode.AlreadyExists" /> and a missing-but-required key is
/// <see cref="StatusCode.InvalidArgument" />, both straight out of
/// <see href="https://google.aip.dev/193">AIP-193</see> — the same table
/// <c>WolverineGrpcExceptionInterceptor</c> already maps ordinary .NET exceptions through, so a gRPC
/// client sees deduplication refusals in exactly the shape it already handles every other refusal in.
/// </para>
///
/// <para>
/// Throwing rather than returning is not a stylistic choice: a unary RPC method has to produce a
/// response message, and there is no honest response body for "this was already done". The status is
/// the answer.
/// </para>
/// </summary>
internal class GrpcDeduplicationStopFrame : SyncFrame
{
    private readonly Variable _condition;
    private readonly StatusCode _statusCode;
    private readonly string _message;

    public GrpcDeduplicationStopFrame(Variable condition, StatusCode statusCode, string message)
    {
        _condition = condition;
        _statusCode = statusCode;
        _message = message.Replace("\"", "'");
        uses.Add(condition);
    }

    public override void GenerateCode(GeneratedMethod method, ISourceWriter writer)
    {
        var statusCode = typeof(StatusCode).FullNameInCode();
        var status = typeof(Status).FullNameInCode();
        var rpcException = typeof(RpcException).FullNameInCode();

        writer.WriteComment($"GH-4180: {_statusCode} for a failed logical deduplication check");
        writer.Write($"BLOCK:if ({_condition.Usage})");
        writer.Write(
            $"throw new {rpcException}(new {status}({statusCode}.{_statusCode}, \"{_message}\"));");
        writer.FinishBlock();

        Next?.GenerateCode(method, writer);
    }
}

/// <summary>
/// GH-4180. Shared implementation of <see cref="IChain.BuildDeduplicationStopCondition" /> for all
/// three gRPC chain flavours — proto-first, code-first, and hand-written. The refusal is identical
/// in each; only the surrounding codegen differs, so the three overrides delegate here rather than
/// repeating the mapping and drifting apart.
/// </summary>
internal static class GrpcDeduplication
{
    public static Frame[] BuildStopCondition(Variable condition, DeduplicationOutcome outcome,
        DeduplicationRequirement requirement)
    {
        var key = requirement.Key ?? DeduplicationRequirement.DefaultHeaderName;

        return outcome switch
        {
            DeduplicationOutcome.Duplicate =>
            [
                new GrpcDeduplicationStopFrame(condition, StatusCode.AlreadyExists,
                    $"A call with this '{key}' has already been handled")
            ],
            DeduplicationOutcome.MissingId =>
            [
                new GrpcDeduplicationStopFrame(condition, StatusCode.InvalidArgument,
                    $"This method requires a logical deduplication id in the '{key}' request metadata")
            ],
            _ => throw new ArgumentOutOfRangeException(nameof(outcome))
        };
    }
}
