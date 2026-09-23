using JasperFx.CodeGeneration;
using JasperFx.CodeGeneration.Frames;
using JasperFx.CodeGeneration.Model;
using JasperFx.Core.Reflection;
using Microsoft.AspNetCore.Http;
using Wolverine.Persistence.Codegen;

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
/// GH-4501. The HTTP failure path for a logical deduplication claim.
///
/// <para>
/// An idempotency key is supposed to mean "this succeeded once", not "this was attempted once". A
/// message handler or a gRPC method only fails by throwing, which the base frame's catch block already
/// compensates for — but an HTTP chain also stops on an error status without throwing: a
/// FluentValidation 400, a <c>ProblemDetails</c> 409 from a <c>Validate</c> method, a
/// <c>[WriteAggregate]</c> 404 on a missing stream. The handler never ran and nothing was written, yet
/// the claim outlived the request; the caller that never saw the failure and retried under the same key
/// was then told "already done" for work that never happened.
/// </para>
///
/// <para>
/// 400 and up, rather than "not 2xx": a 3xx is an answer, not a refusal, and a POST that redirects to
/// the resource it just created has succeeded exactly once and must keep its claim.
/// </para>
/// </summary>
internal class ReleaseDeduplicationIdOnHttpFailureFrame : ReleaseDeduplicationIdOnFailureFrame
{
    private Variable? _httpResponse;
    private Variable? _httpContext;

    public ReleaseDeduplicationIdOnHttpFailureFrame(Variable deduplicationId, Type? ancillaryStoreMarker)
        : base(deduplicationId, ancillaryStoreMarker)
    {
    }

    protected override string BuildReleaseCondition()
        => $"({ThrewFlag} || {_httpResponse!.Usage}.{nameof(HttpResponse.StatusCode)} >= 400)";

    /// <summary>
    /// GH-4547. The finally alone is too late: WriteProblems flushes the failure response and only then
    /// does the finally run the DELETE, so a prompt retry under the same key can beat the release and be
    /// refused as a duplicate for work that never happened. Registering an OnStarting callback here --
    /// before the try, so the claim already exists -- closes the window, because those callbacks are
    /// awaited before the response headers go out. The finally stays for the paths where no response ever
    /// starts.
    /// </summary>
    protected override void writeBeforeTry(ISourceWriter writer)
    {
        writer.WriteComment("GH-4547: release the claim BEFORE a failure response is flushed to the caller");
        writer.Write(
            $"{typeof(HttpHandler).FullNameInCode()}.{nameof(HttpHandler.ReleaseDeduplicationClaimBeforeFailureResponse)}({_httpContext!.Usage}, {DeduplicatorUsage}, {DeduplicationId.Usage}, {AncillaryStoreMarkerUsage});");
    }

    protected override IEnumerable<Variable> FindAdditionalVariables(IMethodVariables chain)
    {
        _httpResponse = chain.FindVariable(typeof(HttpResponse));
        yield return _httpResponse;

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
