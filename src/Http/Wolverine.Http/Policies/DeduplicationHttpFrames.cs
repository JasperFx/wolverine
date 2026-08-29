using JasperFx.CodeGeneration;
using JasperFx.CodeGeneration.Frames;
using JasperFx.CodeGeneration.Model;
using JasperFx.Core.Reflection;
using Microsoft.AspNetCore.Http;

namespace Wolverine.Http.Policies;

/// <summary>
/// GH-4180. The HTTP answer to a failed logical deduplication check.
///
/// <para>
/// Unlike a message handler, an HTTP endpoint owes its caller an answer, so a refusal is a status
/// code with a <c>ProblemDetails</c> body rather than a silent discard. This also gives HTTP the
/// request/reply half of idempotency for free: the second caller gets a meaningful response without
/// Wolverine having to store and replay the original one, which is the much larger Stripe-style
/// idempotency-key feature the issue deliberately scoped out.
/// </para>
///
/// <para>
/// 409 Conflict is the default for a duplicate, and 400 for a missing-but-required key. An
/// application that considers a replayed create benign can ask for 200 or 204 instead —
/// see <c>[Deduplicated]</c>'s HTTP options.
/// </para>
/// </summary>
internal class DeduplicationProblemDetailsFrame : AsyncFrame
{
    private readonly Variable _condition;
    private readonly int _statusCode;
    private readonly string _message;
    private Variable? _httpContext;

    public DeduplicationProblemDetailsFrame(Variable condition, int statusCode, string message)
    {
        _condition = condition;
        _statusCode = statusCode;
        _message = message;
        uses.Add(condition);
    }

    public override void GenerateCode(GeneratedMethod method, ISourceWriter writer)
    {
        writer.WriteComment($"GH-4180: {_statusCode} for a failed logical deduplication check");
        writer.Write($"BLOCK:if ({_condition.Usage})");

        var constant = Constant.For(_message);
        writer.Write(
            $"await {nameof(HttpHandler.WriteProblems)}({_statusCode}, {constant.Usage}, {_httpContext!.Usage}, null);");
        writer.Write("return;");

        writer.FinishBlock();

        Next?.GenerateCode(method, writer);
    }

    public override IEnumerable<Variable> FindVariables(IMethodVariables chain)
    {
        _httpContext = chain.FindVariable(typeof(HttpContext));
        yield return _httpContext;
    }
}

/// <summary>
/// GH-4180. Bare status-code variant, for applications that treat a replayed request as benign and
/// want a 200 or 204 with no body rather than a 409 with a problem document.
/// </summary>
internal class DeduplicationStatusCodeFrame : SyncFrame
{
    private readonly Variable _condition;
    private readonly int _statusCode;
    private Variable? _httpResponse;

    public DeduplicationStatusCodeFrame(Variable condition, int statusCode)
    {
        _condition = condition;
        _statusCode = statusCode;
        uses.Add(condition);
    }

    public override void GenerateCode(GeneratedMethod method, ISourceWriter writer)
    {
        writer.WriteComment($"GH-4180: {_statusCode} for an already-handled logical deduplication id");
        writer.Write($"BLOCK:if ({_condition.Usage})");
        writer.Write($"{_httpResponse!.Usage}.{nameof(HttpResponse.StatusCode)} = {_statusCode};");

        if (method.AsyncMode == AsyncMode.ReturnCompletedTask)
        {
            writer.Write($"return {typeof(Task).FullNameInCode()}.{nameof(Task.CompletedTask)};");
        }
        else
        {
            writer.Write("return;");
        }

        writer.FinishBlock();

        Next?.GenerateCode(method, writer);
    }

    public override IEnumerable<Variable> FindVariables(IMethodVariables chain)
    {
        _httpResponse = chain.FindVariable(typeof(HttpResponse));
        yield return _httpResponse;
    }
}
