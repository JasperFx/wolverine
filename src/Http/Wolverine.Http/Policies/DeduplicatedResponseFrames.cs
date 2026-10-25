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
        var window = WindowUsage(_window);

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
        var window = FSharpWindowUsage(_window);

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

    internal static string WindowUsage(TimeSpan? window) => window.HasValue
        ? $"{typeof(TimeSpan).FullNameInCode()}.{nameof(TimeSpan.FromTicks)}({window.Value.Ticks})"
        : "null";

    // TimeSpan.FromTicks takes an Int64, and an unsuffixed F# integer literal is Int32 -- a window of
    // more than ~3.5 minutes would not even fit one. Hence the `L`. The Nullable is spelled out rather
    // than left to F#'s implicit conversion, so neither branch depends on it.
    internal static string FSharpWindowUsage(TimeSpan? window) => window.HasValue
        ? $"{typeof(TimeSpan?).FSharpName()}({typeof(TimeSpan).FSharpName()}.{nameof(TimeSpan.FromTicks)}({window.Value.Ticks}L))"
        : $"{typeof(TimeSpan?).FSharpName()}()";

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

    /// <summary>
    /// GH-5165. The persistence provider reports when the database commit succeeded, so a failure after it (on
    /// Marten, the outbox flush or a session listener inside <c>SaveChangesAsync</c>) keeps the claim.
    /// </summary>
    public bool KeepClaimOverCommittedWork { get; set; }

    public override void GenerateCode(GeneratedMethod method, ISourceWriter writer)
    {
        var marker = ClaimDeduplicatedResponseFrame.MarkerUsage(_ancillaryStoreMarker);

        writer.WriteComment("GH-4742: give the claim back unless the work was done, before a response is flushed");
        writer.Write(
            $"{typeof(HttpHandler).FullNameInCode()}.{nameof(HttpHandler.ReleaseDeduplicatedResponseBeforeFailureResponse)}({_httpContext!.Usage}, {_responses!.Usage}, {_deduplicationId.Usage}, {_claimToken.Usage}, {marker});");

        writer.Write("BLOCK:try");
        Next?.GenerateCode(method, writer);
        writer.FinishBlock();

        if (KeepClaimOverCommittedWork)
        {
            // GH-5165: a failure over a commit that already succeeded keeps the claim, so the work never runs twice
            writer.Write("BLOCK:catch");
            writer.Write(
                $"{typeof(HttpHandler).FullNameInCode()}.{nameof(HttpHandler.KeepDeduplicatedClaimIfCommitted)}({_httpContext.Usage});");
            writer.Write("throw;");
            writer.FinishBlock();
        }

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

        if (KeepClaimOverCommittedWork)
        {
            // GH-5165: a failure over a commit that already succeeded keeps the claim
            writer.Write(
                $"{typeof(HttpHandler).FSharpName()}.{nameof(HttpHandler.KeepDeduplicatedClaimIfCommitted)}({_httpContext!.FSharpUsage})");
        }

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

/// <summary>
/// GH-5160. The id the store keys a claim by, for a provider's lookup and claim: the hash
/// <see cref="DeduplicatedResponses.StorageIdFor" /> makes, or null when the request has no key.
/// </summary>
internal class DeduplicationStorageIdFrame : SyncFrame
{
    private readonly Variable _deduplicationId;

    public DeduplicationStorageIdFrame(Variable deduplicationId)
    {
        _deduplicationId = deduplicationId;
        Variable = new Variable(typeof(string), "deduplicationStorageId", this);
        uses.Add(deduplicationId);
    }

    public Variable Variable { get; }

    public override void GenerateCode(GeneratedMethod method, ISourceWriter writer)
    {
        writer.Write(
            $"var {Variable.Usage} = string.IsNullOrWhiteSpace({_deduplicationId.Usage}) ? null : {typeof(DeduplicatedResponses).FullNameInCode()}.{nameof(DeduplicatedResponses.StorageIdFor)}({_deduplicationId.Usage});");
        Next?.GenerateCode(method, writer);
    }

    public override void GenerateFSharpCode(GeneratedMethod method, ISourceWriter writer)
    {
        writer.Write(
            $"{Variable.FSharpAssignmentUsage} = if System.String.IsNullOrWhiteSpace({_deduplicationId.FSharpUsage}) then null else {typeof(DeduplicatedResponses).FSharpName()}.{nameof(DeduplicatedResponses.StorageIdFor)}({_deduplicationId.FSharpUsage})");
        Next?.GenerateFSharpCode(method, writer);
    }
}

/// <summary>
/// GH-5160. For a chain whose claim is written inside its own transaction: answers a request whose key an earlier
/// request already committed (the stored response, 422 or 409) and stops, from the claim the provider's lookup read.
/// The refusal half of GH-4505's check-then-refuse. Nothing is claimed here; the claim is written with the work, by
/// <see cref="PrepareDeduplicatedResponseClaimFrame" /> and the provider's claim frame.
/// </summary>
internal class AnswerExistingDeduplicatedResponseFrame : AsyncFrame
{
    private readonly Variable _existingClaim;
    private readonly Variable _fingerprint;
    private readonly string _keyName;
    private Variable? _httpContext;

    public AnswerExistingDeduplicatedResponseFrame(Variable existingClaim, Variable fingerprint, string keyName)
    {
        _existingClaim = existingClaim;
        _fingerprint = fingerprint;
        _keyName = keyName;
    }

    public override void GenerateCode(GeneratedMethod method, ISourceWriter writer)
    {
        writer.WriteComment("GH-5160: answer from an earlier request's committed claim; this one claims inside its own transaction");
        writer.Write($"BLOCK:if ({_existingClaim.Usage} != null)");
        writer.Write(
            $"await {nameof(HttpHandler.AnswerDeduplicatedRepeatAsync)}({_httpContext!.Usage}, {_existingClaim.Usage}, {_fingerprint.Usage}, {Constant.For(_keyName).Usage}).ConfigureAwait(false);");
        writer.Write("return;");
        writer.FinishBlock();

        Next?.GenerateCode(method, writer);
    }

    public override void GenerateFSharpCode(GeneratedMethod method, ISourceWriter writer)
    {
        writer.WriteComment("GH-5160: answer from an earlier request's committed claim; this one claims inside its own transaction");

        // No early `return` in F#: the rest of the chain goes in the `else`, as in ClaimDeduplicatedResponseFrame.
        writer.Write($"BLOCK:if not (isNull {_existingClaim.FSharpUsage}) then");
        writer.Write(
            $"do! this.{nameof(HttpHandler.AnswerDeduplicatedRepeatAsync)}({_httpContext!.FSharpUsage}, {_existingClaim.FSharpUsage}, {_fingerprint.FSharpUsage}, {Constant.For(_keyName).Usage})");
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
        yield return _existingClaim;
        yield return _fingerprint;

        _httpContext = chain.FindVariable(typeof(HttpContext));
        yield return _httpContext;
    }
}

/// <summary>
/// GH-5160. Wraps the commit, and answers a commit that failed because a concurrent request with the same key committed
/// first: both found no claim, both did their work, and the second's commit failed, on the claim's primary key or
/// earlier on the work itself (the same stream, a versioned document). Its work rolled back; it is answered from
/// the first's claim, as a repeat would be. A failure with no such claim to answer from is rethrown unaltered.
///
/// <para>
/// Only the commit: <see cref="MarkDeduplicatedResponseCommittedFrame" /> records that it succeeded, so a failure
/// after it (writing the response) is not mistaken for a lost race, and the endpoint's own failures never reach
/// here. The commit runs before the response is written, so a clean answer can still go out. Marten's outbox flush
/// runs inside the commit call; see <see cref="HttpHandler.TryAnswerDeduplicatedCommitFailureAsync" />.
/// </para>
///
/// <para>
/// The price of claiming at commit, as for <c>RefuseDuplicateClaimAtCommitFrame</c>: the loser does its work and
/// throws it away, where claim-and-release refuses it with 409 before doing any.
/// </para>
/// </summary>
internal class AnswerDeduplicatedResponseRaceFrame : Frame
{
    private const string ExceptionName = "deduplicatedResponseCommitException";

    private readonly Variable _deduplicationId;
    private readonly Variable _fingerprint;
    private readonly Variable _pendingClaim;
    private readonly string _keyName;
    private readonly Type? _ancillaryStoreMarker;
    private Variable? _responses;
    private Variable? _httpContext;

    /// <param name="pendingClaim">
    /// This request's own claim, so a failure after its own database commit is not answered as if a concurrent
    /// request had won.
    /// </param>
    public AnswerDeduplicatedResponseRaceFrame(Variable deduplicationId, Variable fingerprint, Variable pendingClaim,
        string keyName, Type? ancillaryStoreMarker) : base(true)
    {
        _deduplicationId = deduplicationId;
        _fingerprint = fingerprint;
        _pendingClaim = pendingClaim;
        _keyName = keyName;
        _ancillaryStoreMarker = ancillaryStoreMarker;
    }

    public override void GenerateCode(GeneratedMethod method, ISourceWriter writer)
    {
        writer.Write("BLOCK:try");
        Next?.GenerateCode(method, writer);
        writer.FinishBlock();

        writer.Write(
            $"BLOCK:catch ({typeof(Exception).FullNameInCode()} {ExceptionName}) when (!{typeof(HttpHandler).FullNameInCode()}.{nameof(HttpHandler.IsDeduplicatedResponseCommitted)}({_httpContext!.Usage}))");
        writer.WriteComment("GH-5160: the commit failed; answer from a claim a concurrent request committed first, if there is one");
        writer.Write(
            $"BLOCK:if (!await {nameof(HttpHandler.TryAnswerDeduplicatedCommitFailureAsync)}({_httpContext.Usage}, {_responses!.Usage}, {_deduplicationId.Usage}, {_fingerprint.Usage}, {_pendingClaim.Usage}, {Constant.For(_keyName).Usage}, {ClaimDeduplicatedResponseFrame.MarkerUsage(_ancillaryStoreMarker)}, {ExceptionName}).ConfigureAwait(false))");
        writer.Write("throw;");
        writer.FinishBlock();
        writer.FinishBlock();
    }

    public override void GenerateFSharpCode(GeneratedMethod method, ISourceWriter writer)
    {
        // A `with` handler may `let!` inside a task expression, unlike a `finally` (see
        // ReleaseUnansweredDeduplicatedResponseFrame). An exception the guard rejects is rethrown unaltered; one it
        // accepts but cannot answer is rethrown through ExceptionDispatchInfo, as `reraise()` is illegal here.
        writer.Write("BLOCK:try");
        if (Next != null)
        {
            Next.GenerateFSharpCode(method, writer);
        }
        else
        {
            writer.Write("()");
        }

        writer.FinishBlock();

        writer.Write(
            $"BLOCK:with {ExceptionName} when not ({typeof(HttpHandler).FSharpName()}.{nameof(HttpHandler.IsDeduplicatedResponseCommitted)}({_httpContext!.FSharpUsage})) ->");
        writer.WriteComment("GH-5160: the commit failed; answer from a claim a concurrent request committed first, if there is one");
        writer.Write(
            $"let! deduplicatedCommitFailureAnswered = this.{nameof(HttpHandler.TryAnswerDeduplicatedCommitFailureAsync)}({_httpContext.FSharpUsage}, {_responses!.FSharpUsage}, {_deduplicationId.FSharpUsage}, {_fingerprint.FSharpUsage}, {_pendingClaim.FSharpUsage}, {Constant.For(_keyName).Usage}, {ClaimDeduplicatedResponseFrame.FSharpMarkerUsage(_ancillaryStoreMarker)}, {ExceptionName})");
        writer.Write("BLOCK:if not deduplicatedCommitFailureAnswered then");
        writer.Write(
            $"{typeof(System.Runtime.ExceptionServices.ExceptionDispatchInfo).FSharpName()}.Capture({ExceptionName}).Throw()");
        writer.FinishBlock();
        writer.FinishBlock();
    }

    public override IEnumerable<Variable> FindVariables(IMethodVariables chain)
    {
        yield return _deduplicationId;
        yield return _fingerprint;
        yield return _pendingClaim;

        _responses = chain.FindVariable(typeof(DeduplicatedResponses));
        yield return _responses;

        _httpContext = chain.FindVariable(typeof(HttpContext));
        yield return _httpContext;
    }
}

/// <summary>
/// GH-5160. Straight after the commit: from here a failure is not a lost race, so
/// <see cref="AnswerDeduplicatedResponseRaceFrame" /> lets it through.
/// </summary>
internal class MarkDeduplicatedResponseCommittedFrame : SyncFrame
{
    private Variable? _httpContext;

    public override void GenerateCode(GeneratedMethod method, ISourceWriter writer)
    {
        writer.Write(
            $"{typeof(HttpHandler).FullNameInCode()}.{nameof(HttpHandler.MarkDeduplicatedResponseCommitted)}({_httpContext!.Usage});");
        Next?.GenerateCode(method, writer);
    }

    public override void GenerateFSharpCode(GeneratedMethod method, ISourceWriter writer)
    {
        writer.Write(
            $"{typeof(HttpHandler).FSharpName()}.{nameof(HttpHandler.MarkDeduplicatedResponseCommitted)}({_httpContext!.FSharpUsage})");
        Next?.GenerateFSharpCode(method, writer);
    }

    public override IEnumerable<Variable> FindVariables(IMethodVariables chain)
    {
        _httpContext = chain.FindVariable(typeof(HttpContext));
        yield return _httpContext;
    }
}

/// <summary>
/// GH-5160. Just before the commit, after the endpoint has set the status and <c>Location</c>: marks the work done and
/// builds the claim, with its response, for the provider to write in the same transaction. Null when there is no
/// key or the endpoint failed, so nothing is claimed.
/// </summary>
internal class PrepareDeduplicatedResponseClaimFrame : SyncFrame
{
    private readonly Variable _deduplicationId;
    private readonly Variable _fingerprint;
    private readonly Variable _resource;
    private readonly int _missingResourceStatusCode;
    private readonly TimeSpan? _window;
    private Variable? _responses;
    private Variable? _httpContext;

    public PrepareDeduplicatedResponseClaimFrame(Variable deduplicationId, Variable fingerprint, Variable resource,
        int missingResourceStatusCode, TimeSpan? window)
    {
        _deduplicationId = deduplicationId;
        _fingerprint = fingerprint;
        _resource = resource;
        _missingResourceStatusCode = missingResourceStatusCode;
        _window = window;
        uses.Add(resource);
        Variable = new Variable(typeof(PendingDeduplicatedResponseClaim), "pendingDeduplicatedResponseClaim", this);
    }

    public Variable Variable { get; }

    public override void GenerateCode(GeneratedMethod method, ISourceWriter writer)
    {
        writer.WriteComment("GH-5160: the work is done; the claim and its response commit with it, or not at all");
        writer.Write(
            $"var {Variable.Usage} = {_responses!.Usage}.{nameof(DeduplicatedResponses.PrepareClaim)}({_httpContext!.Usage}, {_deduplicationId.Usage}, {_fingerprint.Usage}, {nameof(HttpHandler.CompleteDeduplicatedRequest)}({_httpContext.Usage}, {_deduplicationId.Usage}, {_resource.Usage}, {_missingResourceStatusCode}), {ClaimDeduplicatedResponseFrame.WindowUsage(_window)});");
        Next?.GenerateCode(method, writer);
    }

    public override void GenerateFSharpCode(GeneratedMethod method, ISourceWriter writer)
    {
        writer.WriteComment("GH-5160: the work is done; the claim and its response commit with it, or not at all");

        // As RecordDeduplicatedResponseFrame: an inherited instance method, with its type argument written out.
        var complete =
            $"this.{nameof(HttpHandler.CompleteDeduplicatedRequest)}<{_resource.VariableType.FSharpName()}>({_httpContext!.FSharpUsage}, {_deduplicationId.FSharpUsage}, {_resource.FSharpUsage}, {_missingResourceStatusCode})";

        writer.Write(
            $"{Variable.FSharpAssignmentUsage} = {_responses!.FSharpUsage}.{nameof(DeduplicatedResponses.PrepareClaim)}({_httpContext.FSharpUsage}, {_deduplicationId.FSharpUsage}, {_fingerprint.FSharpUsage}, {complete}, {ClaimDeduplicatedResponseFrame.FSharpWindowUsage(_window)})");
        Next?.GenerateFSharpCode(method, writer);
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
}

/// <summary>
/// GH-5206. After <see cref="AnswerExistingDeduplicatedResponseFrame" /> found no claim: holds back this request's
/// response until its commit, so a failure before the commit (an answer of 400 and up, or a throw) is looked up
/// again and answered from a claim a concurrent request committed meanwhile. See
/// <see cref="HttpHandler.WatchForDeduplicatedRefusal" />.
/// </summary>
internal class WatchForDeduplicatedRefusalFrame : SyncFrame
{
    private readonly Variable _deduplicationId;
    private readonly Variable _fingerprint;
    private readonly string _keyName;
    private readonly Type? _ancillaryStoreMarker;
    private Variable? _responses;
    private Variable? _httpContext;

    public WatchForDeduplicatedRefusalFrame(Variable deduplicationId, Variable fingerprint, string keyName,
        Type? ancillaryStoreMarker)
    {
        _deduplicationId = deduplicationId;
        _fingerprint = fingerprint;
        _keyName = keyName;
        _ancillaryStoreMarker = ancillaryStoreMarker;
    }

    public override void GenerateCode(GeneratedMethod method, ISourceWriter writer)
    {
        writer.WriteComment("GH-5206: until this request commits, a failure is looked up again in case a concurrent request with its key committed first");
        writer.Write(
            $"{nameof(HttpHandler.WatchForDeduplicatedRefusal)}({_httpContext!.Usage}, {_responses!.Usage}, {_deduplicationId.Usage}, {_fingerprint.Usage}, {Constant.For(_keyName).Usage}, {ClaimDeduplicatedResponseFrame.MarkerUsage(_ancillaryStoreMarker)});");
        Next?.GenerateCode(method, writer);
    }

    public override void GenerateFSharpCode(GeneratedMethod method, ISourceWriter writer)
    {
        writer.WriteComment("GH-5206: until this request commits, a failure is looked up again in case a concurrent request with its key committed first");
        writer.Write(
            $"this.{nameof(HttpHandler.WatchForDeduplicatedRefusal)}({_httpContext!.FSharpUsage}, {_responses!.FSharpUsage}, {_deduplicationId.FSharpUsage}, {_fingerprint.FSharpUsage}, {Constant.For(_keyName).Usage}, {ClaimDeduplicatedResponseFrame.FSharpMarkerUsage(_ancillaryStoreMarker)})");
        Next?.GenerateFSharpCode(method, writer);
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
}

/// <summary>
/// GH-5206. For a <c>[Deduplicated]</c> chain whose claim rides its own transaction, after the up-front check found the
/// id unclaimed: holds back the response until <see cref="MarkDeduplicationCommitStartedFrame" />, so a failure before
/// the commit is answered as a duplicate when a concurrent request with the same id committed meanwhile. See
/// <see cref="HttpHandler.WatchForDuplicateRefusal" />.
/// </summary>
internal class WatchForDuplicateRefusalFrame : SyncFrame
{
    private readonly Variable _deduplicationId;
    private readonly Type? _ancillaryStoreMarker;
    private readonly int _statusCode;
    private readonly string? _detail;
    private Variable? _deduplicator;
    private Variable? _httpContext;

    public WatchForDuplicateRefusalFrame(Variable deduplicationId, Type? ancillaryStoreMarker, int statusCode,
        string? detail)
    {
        _deduplicationId = deduplicationId;
        _ancillaryStoreMarker = ancillaryStoreMarker;
        _statusCode = statusCode;
        _detail = detail;
    }

    private string detailUsage => _detail == null ? "null" : Constant.For(_detail).Usage;

    public override void GenerateCode(GeneratedMethod method, ISourceWriter writer)
    {
        writer.WriteComment("GH-5206: until this request commits, a failure is looked up again in case a concurrent request with its id committed first");
        writer.Write(
            $"{nameof(HttpHandler.WatchForDuplicateRefusal)}({_httpContext!.Usage}, {_deduplicator!.Usage}, {_deduplicationId.Usage}, {ClaimDeduplicatedResponseFrame.MarkerUsage(_ancillaryStoreMarker)}, {_statusCode}, {detailUsage});");
        Next?.GenerateCode(method, writer);
    }

    public override void GenerateFSharpCode(GeneratedMethod method, ISourceWriter writer)
    {
        writer.WriteComment("GH-5206: until this request commits, a failure is looked up again in case a concurrent request with its id committed first");
        writer.Write(
            $"this.{nameof(HttpHandler.WatchForDuplicateRefusal)}({_httpContext!.FSharpUsage}, {_deduplicator!.FSharpUsage}, {_deduplicationId.FSharpUsage}, {ClaimDeduplicatedResponseFrame.FSharpMarkerUsage(_ancillaryStoreMarker)}, {_statusCode}, {detailUsage})");
        Next?.GenerateFSharpCode(method, writer);
    }

    public override IEnumerable<Variable> FindVariables(IMethodVariables chain)
    {
        yield return _deduplicationId;

        _deduplicator = chain.FindVariable(typeof(IMessageDeduplicator));
        yield return _deduplicator;

        _httpContext = chain.FindVariable(typeof(HttpContext));
        yield return _httpContext;
    }
}

/// <summary>
/// GH-5206. Just before the commit a <c>[Deduplicated]</c> claim rides: from here a failure is the commit race
/// wrapper's to answer, so <see cref="WatchForDuplicateRefusalFrame" /> lets it through.
/// </summary>
internal class MarkDeduplicationCommitStartedFrame : SyncFrame
{
    private Variable? _httpContext;

    public override void GenerateCode(GeneratedMethod method, ISourceWriter writer)
    {
        writer.Write(
            $"{typeof(HttpHandler).FullNameInCode()}.{nameof(HttpHandler.MarkDeduplicationCommitStarted)}({_httpContext!.Usage});");
        Next?.GenerateCode(method, writer);
    }

    public override void GenerateFSharpCode(GeneratedMethod method, ISourceWriter writer)
    {
        writer.Write(
            $"{typeof(HttpHandler).FSharpName()}.{nameof(HttpHandler.MarkDeduplicationCommitStarted)}({_httpContext!.FSharpUsage})");
        Next?.GenerateFSharpCode(method, writer);
    }

    public override IEnumerable<Variable> FindVariables(IMethodVariables chain)
    {
        _httpContext = chain.FindVariable(typeof(HttpContext));
        yield return _httpContext;
    }
}
