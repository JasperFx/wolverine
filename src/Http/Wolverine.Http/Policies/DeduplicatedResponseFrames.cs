using JasperFx.CodeGeneration;
using JasperFx.CodeGeneration.Frames;
using JasperFx.CodeGeneration.Model;
using JasperFx.Core.Reflection;
using Microsoft.AspNetCore.Http;
using Wolverine.Configuration;
using Wolverine.Http.Runtime;
using Wolverine.Persistence;
using Wolverine.Persistence.Durability;

namespace Wolverine.Http.Policies;

/// <summary>
/// GH-4742. Makes the body rewindable for the fingerprint. Inserted after <c>DetermineFrames</c> has placed
/// everything that reads the body, including the audit frame, so it is emitted first.
/// </summary>
internal class EnableRequestBufferingFrame : SyncFrame
{
    private Variable? _httpContext;

    public override void GenerateCode(GeneratedMethod method, ISourceWriter writer)
    {
        writer.WriteComment("GH-4742: buffer the body so the deduplication fingerprint can read it after binding");
        writer.Write(
            $"{typeof(HttpRequestRewindExtensions).FullNameInCode()}.{nameof(HttpRequestRewindExtensions.EnableBuffering)}({_httpContext!.Usage}.{nameof(HttpContext.Request)});");
        Next?.GenerateCode(method, writer);
    }

    public override void GenerateFSharpCode(GeneratedMethod method, ISourceWriter writer)
    {
        writer.WriteComment("GH-4742: buffer the body so the deduplication fingerprint can read it after binding");

        // EnableBuffering is a void extension method; called statically it is a unit-valued statement in F#
        // exactly as in C#. The single-argument overload is unambiguous.
        writer.Write(
            $"{typeof(HttpRequestRewindExtensions).FSharpName()}.{nameof(HttpRequestRewindExtensions.EnableBuffering)}({_httpContext!.FSharpUsage}.{nameof(HttpContext.Request)})");
        Next?.GenerateFSharpCode(method, writer);
    }

    public override IEnumerable<Variable> FindVariables(IMethodVariables chain)
    {
        _httpContext = chain.FindVariable(typeof(HttpContext));
        yield return _httpContext;
    }
}

/// <summary>
/// GH-4742. Produces the scoped key every later frame works on. The tenant id is resolved here as a dependency,
/// so tenant detection is emitted before this frame whatever the middleware order.
/// </summary>
internal class ScopeDeduplicationIdFrame : SyncFrame
{
    private readonly Variable _key;
    private readonly DeduplicationScope _scope;
    private readonly string _chainDescription;
    private Variable? _httpContext;
    private Variable? _tenantId;

    public ScopeDeduplicationIdFrame(Variable key, DeduplicationScope scope, string chainDescription)
    {
        _key = key;
        _scope = scope;
        _chainDescription = chainDescription;
        Variable = new Variable(typeof(string), "scopedDeduplicationId", this);
    }

    public Variable Variable { get; }

    public override void GenerateCode(GeneratedMethod method, ISourceWriter writer)
    {
        writer.WriteComment($"GH-4742: the deduplication key is unique within {_scope}");
        writer.Write(
            $"var {Variable.Usage} = {typeof(HttpHandler).FullNameInCode()}.{nameof(HttpHandler.ScopeDeduplicationId)}({_httpContext!.Usage}, {_key.Usage}, ({typeof(DeduplicationScope).FullNameInCode()}){(int)_scope}, {_tenantId?.Usage ?? "null"});");
        Next?.GenerateCode(method, writer);
    }

    public override void GenerateFSharpCode(GeneratedMethod method, ISourceWriter writer)
    {
        writer.WriteComment($"GH-4742: the deduplication key is unique within {_scope}");

        // F# has no cast syntax for an integer-to-enum conversion; `enum<T>` is the equivalent of C#'s
        // `(T)n` for an enum whose underlying type is Int32, which DeduplicationScope's is.
        var scope = $"enum<{typeof(DeduplicationScope).FSharpName()}>({(int)_scope})";

        writer.Write(
            $"{Variable.FSharpAssignmentUsage} = {typeof(HttpHandler).FSharpName()}.{nameof(HttpHandler.ScopeDeduplicationId)}({_httpContext!.FSharpUsage}, {_key.FSharpUsage}, {scope}, {_tenantId?.FSharpUsage ?? "null"})");
        Next?.GenerateFSharpCode(method, writer);
    }

    public override IEnumerable<Variable> FindVariables(IMethodVariables chain)
    {
        yield return _key;

        _httpContext = chain.FindVariable(typeof(HttpContext));
        yield return _httpContext;

        if (!_scope.HasFlag(DeduplicationScope.Tenant)) yield break;

        // Refuse rather than collapse every tenant into one namespace.
        if (!chain.TryFindVariableByName(typeof(string), PersistenceConstants.TenantIdVariableName, out var tenant))
        {
            throw new InvalidOperationException(
                $"{_chainDescription} asks for [DeduplicatedWithResponse({nameof(DeduplicationScope)}.{nameof(DeduplicationScope.Tenant)})], but has no tenant id detection to scope by. Configure it with WolverineHttpOptions.TenantId, or drop Tenant from the scope. See GH-4742");
        }

        _tenantId = tenant;
        yield return _tenantId;
    }
}

/// <summary>GH-4742. Fingerprints the request for the claim to store.</summary>
internal class DeduplicationFingerprintFrame : AsyncFrame
{
    private readonly Variable _deduplicationId;
    private Variable? _httpContext;

    public DeduplicationFingerprintFrame(Variable deduplicationId)
    {
        _deduplicationId = deduplicationId;
        Variable = new Variable(typeof(string), "deduplicationFingerprint", this);
    }

    public Variable Variable { get; }

    public override void GenerateCode(GeneratedMethod method, ISourceWriter writer)
    {
        writer.WriteComment("GH-4742: what a repeat must match to be answered with the stored response");
        writer.Write(
            $"var {Variable.Usage} = await {typeof(HttpHandler).FullNameInCode()}.{nameof(HttpHandler.ComputeDeduplicationFingerprintAsync)}({_httpContext!.Usage}, {_deduplicationId.Usage}).ConfigureAwait(false);");
        Next?.GenerateCode(method, writer);
    }

    public override void GenerateFSharpCode(GeneratedMethod method, ISourceWriter writer)
    {
        writer.WriteComment("GH-4742: what a repeat must match to be answered with the stored response");

        // `let!` inside the enclosing `task { }` body; `.ConfigureAwait(false)` is dropped because the
        // computation expression controls scheduling.
        writer.Write(
            $"let! {Variable.Usage} = {typeof(HttpHandler).FSharpName()}.{nameof(HttpHandler.ComputeDeduplicationFingerprintAsync)}({_httpContext!.FSharpUsage}, {_deduplicationId.FSharpUsage})");
        Next?.GenerateFSharpCode(method, writer);
    }

    public override IEnumerable<Variable> FindVariables(IMethodVariables chain)
    {
        yield return _deduplicationId;

        _httpContext = chain.FindVariable(typeof(HttpContext));
        yield return _httpContext;
    }
}

/// <summary>
/// GH-4742. Claims the key with the fingerprint. A request that loses the claim is answered from it (the stored
/// response, 422 or 409) and stops.
/// </summary>
internal class ClaimDeduplicatedResponseFrame : AsyncFrame
{
    private readonly Variable _deduplicationId;
    private readonly Variable _fingerprint;
    private readonly TimeSpan? _window;
    private readonly Type? _ancillaryStoreMarker;
    private readonly string _keyName;
    private Variable? _responses;
    private Variable? _httpContext;

    public ClaimDeduplicatedResponseFrame(Variable deduplicationId, Variable fingerprint, TimeSpan? window,
        Type? ancillaryStoreMarker, string keyName)
    {
        _deduplicationId = deduplicationId;
        _fingerprint = fingerprint;
        _window = window;
        _ancillaryStoreMarker = ancillaryStoreMarker;
        _keyName = keyName;
        Variable = new Variable(typeof(DeduplicatedResponseClaim), "deduplicatedResponseClaim", this);
        ClaimToken = new Variable(typeof(string), "deduplicationClaimToken", this);
    }

    public Variable Variable { get; }

    /// <summary>Unique to this attempt: only its holder may record on or release the claim.</summary>
    public Variable ClaimToken { get; }

    public override void GenerateCode(GeneratedMethod method, ISourceWriter writer)
    {
        var window = _window.HasValue
            ? $"{typeof(TimeSpan).FullNameInCode()}.{nameof(TimeSpan.FromTicks)}({_window.Value.Ticks})"
            : "null";

        writer.WriteComment("GH-4742: claim the key, or answer from the claim this request lost to");
        writer.Write(
            $"var {ClaimToken.Usage} = {typeof(Guid).FullNameInCode()}.{nameof(Guid.NewGuid)}().{nameof(Guid.ToString)}();");
        writer.Write(
            $"var {Variable.Usage} = await {_responses!.Usage}.{nameof(DeduplicatedResponses.TryClaimAsync)}({_deduplicationId.Usage}, {_fingerprint.Usage}, {ClaimToken.Usage}, {window}, {MarkerUsage(_ancillaryStoreMarker)}).ConfigureAwait(false);");
        writer.Write($"BLOCK:if ({Variable.Usage} != null)");
        writer.Write(
            $"await {nameof(HttpHandler.AnswerDeduplicatedRepeatAsync)}({_httpContext!.Usage}, {Variable.Usage}, {_fingerprint.Usage}, {Constant.For(_keyName).Usage}).ConfigureAwait(false);");
        writer.Write("return;");
        writer.FinishBlock();

        Next?.GenerateCode(method, writer);
    }

    public override void GenerateFSharpCode(GeneratedMethod method, ISourceWriter writer)
    {
        // TimeSpan.FromTicks takes an Int64, and an unsuffixed F# integer literal is Int32 -- a window of
        // more than ~3.5 minutes would not even fit one. Hence the `L`. The Nullable is spelled out rather
        // than left to F#'s implicit conversion, so neither branch depends on it.
        var window = _window.HasValue
            ? $"{typeof(TimeSpan?).FSharpName()}({typeof(TimeSpan).FSharpName()}.{nameof(TimeSpan.FromTicks)}({_window.Value.Ticks}L))"
            : $"{typeof(TimeSpan?).FSharpName()}()";

        writer.WriteComment("GH-4742: claim the key, or answer from the claim this request lost to");
        writer.Write(
            $"{ClaimToken.FSharpAssignmentUsage} = {typeof(Guid).FSharpName()}.{nameof(Guid.NewGuid)}().{nameof(Guid.ToString)}()");
        writer.Write(
            $"let! {Variable.Usage} = {_responses!.FSharpUsage}.{nameof(DeduplicatedResponses.TryClaimAsync)}({_deduplicationId.FSharpUsage}, {_fingerprint.FSharpUsage}, {ClaimToken.FSharpUsage}, {window}, {FSharpMarkerUsage(_ancillaryStoreMarker)})");

        // F# has no early `return`, so the C# `if (claim != null) { answer; return; }` becomes an if/else
        // whose `else` carries the rest of the chain -- the same shape FSharpEmitHelpers.WriteAbortGuard
        // emits, except that this frame's abort branch does work (answering the repeat) rather than
        // falling through to a no-op. AnswerDeduplicatedRepeatAsync is an inherited INSTANCE method on
        // HttpHandler, so it needs the generated member's `this` self identifier (jasperfx#393).
        writer.Write($"BLOCK:if not (isNull {Variable.FSharpUsage}) then");
        writer.Write(
            $"do! this.{nameof(HttpHandler.AnswerDeduplicatedRepeatAsync)}({_httpContext!.FSharpUsage}, {Variable.FSharpUsage}, {_fingerprint.FSharpUsage}, {Constant.For(_keyName).Usage})");
        writer.FinishBlock();

        writer.Write("BLOCK:else");
        if (Next != null)
        {
            Next.GenerateFSharpCode(method, writer);
        }
        else
        {
            writer.Write(FSharpEmitHelpers.AbortExpression(method));
        }

        writer.FinishBlock();
    }

    public override IEnumerable<Variable> FindVariables(IMethodVariables chain)
    {
        yield return _deduplicationId;
        yield return _fingerprint;

        _responses = chain.FindVariable(typeof(DeduplicatedResponses));
        yield return _responses;

        _httpContext = chain.FindVariable(typeof(HttpContext));
        yield return _httpContext;
    }

    internal static string MarkerUsage(Type? ancillaryStoreMarker) => ancillaryStoreMarker == null
        ? "null"
        : $"typeof({ancillaryStoreMarker.FullNameInCode()})";

    /// <summary>F#'s <c>typeof&lt;T&gt;</c> rather than C#'s <c>typeof(T)</c>.</summary>
    internal static string FSharpMarkerUsage(Type? ancillaryStoreMarker) => ancillaryStoreMarker == null
        ? "null"
        : $"typeof<{ancillaryStoreMarker.FSharpName()}>";
}

/// <summary>
/// GH-4742. Gives the claim back unless the endpoint's work was done: on a throw, a failure status, or any early
/// exit, including a successful one from middleware. A response that starts first releases it before it is
/// flushed (GH-4547). Once the work is done the claim is kept, so it never runs twice within the window.
/// </summary>
internal class ReleaseUnansweredDeduplicatedResponseFrame : AsyncFrame
{
    private readonly Variable _deduplicationId;
    private readonly Variable _claimToken;
    private readonly Type? _ancillaryStoreMarker;
    private Variable? _responses;
    private Variable? _httpContext;

    public ReleaseUnansweredDeduplicatedResponseFrame(Variable deduplicationId, Variable claimToken,
        Type? ancillaryStoreMarker)
    {
        _deduplicationId = deduplicationId;
        _claimToken = claimToken;
        _ancillaryStoreMarker = ancillaryStoreMarker;
    }

    public override void GenerateCode(GeneratedMethod method, ISourceWriter writer)
    {
        var marker = ClaimDeduplicatedResponseFrame.MarkerUsage(_ancillaryStoreMarker);

        writer.WriteComment("GH-4742: give the claim back unless the work was done, before a response is flushed");
        writer.Write(
            $"{typeof(HttpHandler).FullNameInCode()}.{nameof(HttpHandler.ReleaseDeduplicatedResponseBeforeFailureResponse)}({_httpContext!.Usage}, {_responses!.Usage}, {_deduplicationId.Usage}, {_claimToken.Usage}, {marker});");

        writer.Write("BLOCK:try");
        Next?.GenerateCode(method, writer);
        writer.FinishBlock();

        writer.Write("BLOCK:finally");
        writer.Write(
            $"BLOCK:if (!{typeof(HttpHandler).FullNameInCode()}.{nameof(HttpHandler.IsDeduplicatedWorkDone)}({_httpContext.Usage}))");
        writer.Write(
            $"await {_responses.Usage}.{nameof(DeduplicatedResponses.ReleaseUnansweredAsync)}({_deduplicationId.Usage}, {_claimToken.Usage}, {marker}).ConfigureAwait(false);");
        writer.FinishBlock();
        writer.FinishBlock();
    }

    public override void GenerateFSharpCode(GeneratedMethod method, ISourceWriter writer)
    {
        var marker = ClaimDeduplicatedResponseFrame.FSharpMarkerUsage(_ancillaryStoreMarker);

        writer.WriteComment("GH-4742: give the claim back unless the work was done, before a response is flushed");
        writer.Write(
            $"{typeof(HttpHandler).FSharpName()}.{nameof(HttpHandler.ReleaseDeduplicatedResponseBeforeFailureResponse)}({_httpContext!.FSharpUsage}, {_responses!.FSharpUsage}, {_deduplicationId.FSharpUsage}, {_claimToken.FSharpUsage}, {marker})");

        // The C# emission is a try/FINALLY. F# cannot express that here: a computation expression's
        // `try ... finally` body must be a synchronous unit expression -- `do!` is illegal inside it
        // (FS0793) -- and ReleaseUnansweredAsync is awaited. So the finally is split into the two paths it
        // actually covers, which between them are exactly equivalent:
        //
        //   * the normal path, including every early exit, becomes the release guard emitted AFTER the rest
        //     of the chain but still inside the `try`. C#'s early `return` inside the try reaches the
        //     finally; F# has no early return, so an aborting frame renders the remainder inside its own
        //     `else` and execution simply falls through to the guard here;
        //   * the throwing path becomes the same guard in a `with` handler that rethrows.
        //
        // The guard is therefore emitted twice on purpose. `reraise()` is illegal inside a computation
        // expression (FS0413), so the rethrow preserves the original stack trace via ExceptionDispatchInfo,
        // as EnrollDbContextInTransaction does.
        writer.Write("BLOCK:try");
        Next?.GenerateFSharpCode(method, writer);
        writeFSharpReleaseGuard(writer, marker);
        writer.FinishBlock();

        writer.Write("BLOCK:with ex ->");
        writeFSharpReleaseGuard(writer, marker);
        writer.Write(
            $"{typeof(System.Runtime.ExceptionServices.ExceptionDispatchInfo).FSharpName()}.Capture(ex).Throw()");
        writer.FinishBlock();
    }

    private void writeFSharpReleaseGuard(ISourceWriter writer, string marker)
    {
        writer.Write(
            $"BLOCK:if not ({typeof(HttpHandler).FSharpName()}.{nameof(HttpHandler.IsDeduplicatedWorkDone)}({_httpContext!.FSharpUsage})) then");
        writer.Write(
            $"do! {_responses!.FSharpUsage}.{nameof(DeduplicatedResponses.ReleaseUnansweredAsync)}({_deduplicationId.FSharpUsage}, {_claimToken.FSharpUsage}, {marker})");
        writer.FinishBlock();
    }

    public override IEnumerable<Variable> FindVariables(IMethodVariables chain)
    {
        yield return _deduplicationId;
        yield return _claimToken;

        _responses = chain.FindVariable(typeof(DeduplicatedResponses));
        yield return _responses;

        _httpContext = chain.FindVariable(typeof(HttpContext));
        yield return _httpContext;
    }
}

/// <summary>
/// GH-4742. Marks the work done and records the response on the claim, after the endpoint and any commit and
/// before the response is written or cascaded messages flush.
/// </summary>
internal class RecordDeduplicatedResponseFrame : AsyncFrame
{
    private readonly Variable _deduplicationId;
    private readonly Variable _claimToken;
    private readonly Variable _resource;
    private readonly int _missingResourceStatusCode;
    private readonly Type? _ancillaryStoreMarker;
    private Variable? _responses;
    private Variable? _httpContext;

    public RecordDeduplicatedResponseFrame(Variable deduplicationId, Variable claimToken, Variable resource,
        int missingResourceStatusCode, Type? ancillaryStoreMarker)
    {
        _deduplicationId = deduplicationId;
        _claimToken = claimToken;
        _resource = resource;
        _missingResourceStatusCode = missingResourceStatusCode;
        _ancillaryStoreMarker = ancillaryStoreMarker;
        uses.Add(resource);
    }

    public override void GenerateCode(GeneratedMethod method, ISourceWriter writer)
    {
        writer.WriteComment("GH-4742: the work is done; record the response, so a repeat is answered with it");
        writer.Write(
            $"await {_responses!.Usage}.{nameof(DeduplicatedResponses.RecordResponseAsync)}({_deduplicationId.Usage}, {_claimToken.Usage}, {nameof(HttpHandler.CompleteDeduplicatedRequest)}({_httpContext!.Usage}, {_deduplicationId.Usage}, {_resource.Usage}, {_missingResourceStatusCode}), {ClaimDeduplicatedResponseFrame.MarkerUsage(_ancillaryStoreMarker)}).ConfigureAwait(false);");
        Next?.GenerateCode(method, writer);
    }

    public override void GenerateFSharpCode(GeneratedMethod method, ISourceWriter writer)
    {
        writer.WriteComment("GH-4742: the work is done; record the response, so a repeat is answered with it");

        // CompleteDeduplicatedRequest<T> is an inherited INSTANCE method, so it needs the generated
        // member's `this` self identifier (jasperfx#393). Its type argument is written out rather than
        // inferred from the resource, which F# would otherwise have to pick up through the `T?`
        // parameter.
        var complete =
            $"this.{nameof(HttpHandler.CompleteDeduplicatedRequest)}<{_resource.VariableType.FSharpName()}>({_httpContext!.FSharpUsage}, {_deduplicationId.FSharpUsage}, {_resource.FSharpUsage}, {_missingResourceStatusCode})";

        writer.Write(
            $"do! {_responses!.FSharpUsage}.{nameof(DeduplicatedResponses.RecordResponseAsync)}({_deduplicationId.FSharpUsage}, {_claimToken.FSharpUsage}, {complete}, {ClaimDeduplicatedResponseFrame.FSharpMarkerUsage(_ancillaryStoreMarker)})");
        Next?.GenerateFSharpCode(method, writer);
    }

    public override IEnumerable<Variable> FindVariables(IMethodVariables chain)
    {
        yield return _deduplicationId;
        yield return _claimToken;

        _responses = chain.FindVariable(typeof(DeduplicatedResponses));
        yield return _responses;

        _httpContext = chain.FindVariable(typeof(HttpContext));
        yield return _httpContext;
    }
}
