using JasperFx.CodeGeneration;
using JasperFx.CodeGeneration.Frames;
using JasperFx.CodeGeneration.Model;
using JasperFx.Core.Reflection;
using Microsoft.AspNetCore.Http;
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
    }

    public Variable Variable { get; }

    public override void GenerateCode(GeneratedMethod method, ISourceWriter writer)
    {
        var window = _window.HasValue
            ? $"{typeof(TimeSpan).FullNameInCode()}.{nameof(TimeSpan.FromTicks)}({_window.Value.Ticks})"
            : "null";

        writer.WriteComment("GH-4742: claim the key, or answer from the claim this request lost to");
        writer.Write(
            $"var {Variable.Usage} = await {_responses!.Usage}.{nameof(DeduplicatedResponses.TryClaimAsync)}({_deduplicationId.Usage}, {_fingerprint.Usage}, {window}, {MarkerUsage(_ancillaryStoreMarker)}).ConfigureAwait(false);");
        writer.Write($"BLOCK:if ({Variable.Usage} != null)");
        writer.Write(
            $"await {nameof(HttpHandler.AnswerDeduplicatedRepeatAsync)}({_httpContext!.Usage}, {Variable.Usage}, {_fingerprint.Usage}, {Constant.For(_keyName).Usage}).ConfigureAwait(false);");
        writer.Write("return;");
        writer.FinishBlock();

        Next?.GenerateCode(method, writer);
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
}

/// <summary>
/// GH-4742. Gives the claim back unless the endpoint's work was done: on a throw, a failure status, or any early
/// exit, including a successful one from middleware. A response that starts first releases it before it is
/// flushed (GH-4547). Once the work is done the claim is kept, so it never runs twice within the window.
/// </summary>
internal class ReleaseUnansweredDeduplicatedResponseFrame : AsyncFrame
{
    private readonly Variable _deduplicationId;
    private readonly Type? _ancillaryStoreMarker;
    private Variable? _responses;
    private Variable? _httpContext;

    public ReleaseUnansweredDeduplicatedResponseFrame(Variable deduplicationId, Type? ancillaryStoreMarker)
    {
        _deduplicationId = deduplicationId;
        _ancillaryStoreMarker = ancillaryStoreMarker;
    }

    public override void GenerateCode(GeneratedMethod method, ISourceWriter writer)
    {
        var marker = ClaimDeduplicatedResponseFrame.MarkerUsage(_ancillaryStoreMarker);

        writer.WriteComment("GH-4742: give the claim back unless the work was done, before a response is flushed");
        writer.Write(
            $"{typeof(HttpHandler).FullNameInCode()}.{nameof(HttpHandler.ReleaseDeduplicatedResponseBeforeFailureResponse)}({_httpContext!.Usage}, {_responses!.Usage}, {_deduplicationId.Usage}, {marker});");

        writer.Write("BLOCK:try");
        Next?.GenerateCode(method, writer);
        writer.FinishBlock();

        writer.Write("BLOCK:finally");
        writer.Write(
            $"BLOCK:if (!{typeof(HttpHandler).FullNameInCode()}.{nameof(HttpHandler.IsDeduplicatedWorkDone)}({_httpContext.Usage}))");
        writer.Write(
            $"await {_responses.Usage}.{nameof(DeduplicatedResponses.ReleaseUnansweredAsync)}({_deduplicationId.Usage}, {marker}).ConfigureAwait(false);");
        writer.FinishBlock();
        writer.FinishBlock();
    }

    public override IEnumerable<Variable> FindVariables(IMethodVariables chain)
    {
        yield return _deduplicationId;

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
    private readonly Variable _resource;
    private readonly int _missingResourceStatusCode;
    private readonly Type? _ancillaryStoreMarker;
    private Variable? _responses;
    private Variable? _httpContext;

    public RecordDeduplicatedResponseFrame(Variable deduplicationId, Variable resource, int missingResourceStatusCode,
        Type? ancillaryStoreMarker)
    {
        _deduplicationId = deduplicationId;
        _resource = resource;
        _missingResourceStatusCode = missingResourceStatusCode;
        _ancillaryStoreMarker = ancillaryStoreMarker;
        uses.Add(resource);
    }

    public override void GenerateCode(GeneratedMethod method, ISourceWriter writer)
    {
        writer.WriteComment("GH-4742: the work is done; record the response, so a repeat is answered with it");
        writer.Write(
            $"await {_responses!.Usage}.{nameof(DeduplicatedResponses.RecordResponseAsync)}({_deduplicationId.Usage}, {nameof(HttpHandler.CompleteDeduplicatedRequest)}({_httpContext!.Usage}, {_deduplicationId.Usage}, {_resource.Usage}, {_missingResourceStatusCode}), {ClaimDeduplicatedResponseFrame.MarkerUsage(_ancillaryStoreMarker)}).ConfigureAwait(false);");
        Next?.GenerateCode(method, writer);
    }

    public override IEnumerable<Variable> FindVariables(IMethodVariables chain)
    {
        yield return _deduplicationId;

        _responses = chain.FindVariable(typeof(DeduplicatedResponses));
        yield return _responses;

        _httpContext = chain.FindVariable(typeof(HttpContext));
        yield return _httpContext;
    }
}
