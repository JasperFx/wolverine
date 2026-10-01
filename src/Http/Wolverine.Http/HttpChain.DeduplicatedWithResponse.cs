using JasperFx.CodeGeneration.Frames;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Wolverine.Http.CodeGen;
using Wolverine.Http.Policies;
using Wolverine.Persistence;
using Wolverine.Persistence.Codegen;

namespace Wolverine.Http;

// GH-4742. [DeduplicatedWithResponse], woven on its own: nothing here touches the [Deduplicated] path.
public partial class HttpChain
{
    /// <summary>
    /// GH-4742. Deduplicate on an idempotency key and answer a repeat with the first response, or null for
    /// none. See <see cref="DeduplicatedWithResponseAttribute" />.
    /// </summary>
    public DeduplicatedWithResponseRequirement? DeduplicatedWithResponse { get; set; }

    private const DeduplicationScope KnownDeduplicationScopes =
        DeduplicationScope.Tenant | DeduplicationScope.User | DeduplicationScope.Endpoint;

    /// <summary>
    /// Refuses a defect in the endpoint's own declaration, which every host that discovers it shares, and
    /// registers the refusal codes before the metadata is built.
    /// </summary>
    private void validateDeduplicatedWithResponse()
    {
        if (DeduplicatedWithResponse is not { } requirement) return;

        assertDeduplicatedWithResponseIsValid(requirement);
        _validatedDeduplicatedWithResponse = requirement;

        if (requirement.Required && !_producesMissingKey)
        {
            Metadata.Produces<ProblemDetails>(StatusCodes.Status400BadRequest, "application/problem+json");
            _producesMissingKey = true;
        }

        if (!_producesRefusals)
        {
            Metadata.Produces<ProblemDetails>(StatusCodes.Status409Conflict, "application/problem+json");
            Metadata.Produces<ProblemDetails>(StatusCodes.Status422UnprocessableEntity, "application/problem+json");
            _producesRefusals = true;
        }
    }

    private DeduplicatedWithResponseRequirement? _validatedDeduplicatedWithResponse;
    private bool _producesMissingKey;
    private bool _producesRefusals;

    /// <summary>
    /// After the policies, before the endpoint is built: validate and describe a requirement a policy set, at
    /// startup like one from the attribute.
    /// </summary>
    internal void FinalizeDeduplicatedWithResponse()
    {
        if (!ReferenceEquals(DeduplicatedWithResponse, _validatedDeduplicatedWithResponse))
        {
            validateDeduplicatedWithResponse();
        }
    }

    private void assertDeduplicatedWithResponseIsValid(DeduplicatedWithResponseRequirement requirement)
    {
        if (requirement.Scope == DeduplicationScope.None)
        {
            throw new InvalidOperationException(
                $"[DeduplicatedWithResponse] on {Description} needs a {nameof(DeduplicationScope)}: a stored response is replayed to anyone who presents the same key and request. Use DeduplicationScope.User, or Tenant | User. See GH-4742");
        }

        // An undefined bit would pass as "scoped" while scoping by nothing.
        if ((requirement.Scope & ~KnownDeduplicationScopes) != 0)
        {
            throw new InvalidOperationException(
                $"[DeduplicatedWithResponse] on {Description} has an unknown {nameof(DeduplicationScope)} value {(int)requirement.Scope}. See GH-4742");
        }

        if (Deduplication != null)
        {
            throw new InvalidOperationException(
                $"{Description} has both [Deduplicated] and [DeduplicatedWithResponse]. Use one. See GH-4742");
        }

        if (!HasResourceType())
        {
            throw new NotSupportedException(
                $"[DeduplicatedWithResponse] on {Description} returns no resource, so a repeat has no response to replay. Use [Deduplicated] instead. See GH-4742");
        }

        if (BindsFormValues)
        {
            // The fingerprint reads the body after binding, which needs a rewindable stream --
            // EnableRequestBufferingFrame, at Middleware[0]. A form value's creator frame is never IN
            // Middleware (HttpChain.BindsFormValues says why), so the arranger hoists the form read ahead of
            // the buffering and ComputeDeduplicationFingerprintAsync then fails its !Body.CanSeek guard on
            // EVERY request. Refuse while the chain is being built rather than 500 at runtime.
            throw new NotSupportedException(
                $"[DeduplicatedWithResponse] on {Description} binds a form value, and form-encoded requests are not supported by this attribute: the request fingerprint needs to re-read the body after binding, and the form read consumes it first. Take a JSON body instead, or use [Deduplicated], which does not fingerprint the request. See GH-4742");
        }
    }

    /// <summary>
    /// Front of the middleware, as for [Deduplicated]: refuse a repeat before any work happens. Called from
    /// AssembleTypes, and idempotent.
    /// </summary>
    private void applyDeduplicatedWithResponse()
    {
        if (DeduplicatedWithResponse is not { } requirement) return;
        if (Middleware.OfType<ClaimDeduplicatedResponseFrame>().Any()) return;

        // Again: a policy may have set either requirement since construction.
        assertDeduplicatedWithResponseIsValid(requirement);

        var key = ResolveDeduplicationId(new DeduplicationRequirement
        {
            Source = requirement.Source,
            Key = requirement.Key
        });

        var frames = new List<Frame>();

        if (requirement.Required)
        {
            var missing = new DeduplicationIdMissingFrame(key);
            frames.Add(missing);
            frames.Add(new DeduplicationProblemDetailsFrame(missing.Variable, StatusCodes.Status400BadRequest,
                $"This endpoint requires a '{requirement.KeyName}' idempotency key"));
        }

        var scoped = new ScopeDeduplicationIdFrame(key, requirement.Scope, Description);
        var fingerprint = new DeduplicationFingerprintFrame(scoped.Variable);

        frames.Add(scoped);
        frames.Add(fingerprint);
        var claim = new ClaimDeduplicatedResponseFrame(scoped.Variable, fingerprint.Variable, requirement.Window,
            AncillaryStoreType, requirement.KeyName);
        frames.Add(claim);
        frames.Add(new ReleaseUnansweredDeduplicatedResponseFrame(scoped.Variable, claim.ClaimToken, AncillaryStoreType));

        Middleware.InsertRange(0, frames);

        // After IHttpAware and any commit, before the flush. The response writer is appended later, in
        // DetermineFrames, so this lands before it too.
        //
        // Both shapes of flush have to be matched. A persistence provider's own flush frame implements
        // IFlushesMessages (EF Core's FlushOutboxAfterCommit); the plain FlushOutgoingMessages that a chain
        // with no provider gets does NOT implement it, which is the whole point of the interface -- it marks
        // "something else already flushes, do not add one of these". GH-4742 shipped with only the
        // FlushOutgoingMessages half, so on EF Core the FindIndex returned -1, the record frame was appended
        // LAST, and a flush that threw left committed work with an unanswered claim: the finally released it
        // and the client's retry re-ran the handler over work that was already done.
        var flush = Postprocessors.FindIndex(x => x is FlushOutgoingMessages or IFlushesMessages);
        Postprocessors.Insert(flush < 0 ? Postprocessors.Count : flush,
            new RecordDeduplicatedResponseFrame(scoped.Variable, claim.ClaimToken, ResourceVariable ?? Method.Creates.First(),
                MissingResponseBodyStatusCode, AncillaryStoreType));
    }

    /// <summary>Called from DetermineFrames once the response writer is chosen.</summary>
    private void assertDeduplicatedWithResponseIsJson()
    {
        // The stored body is System.Text.Json; any other writer would replay different bytes.
        if (DeduplicatedWithResponse != null && !Postprocessors.OfType<WriteJsonFrame>().Any())
        {
            throw new NotSupportedException(
                $"{Description} has [DeduplicatedWithResponse], which replays the response as System.Text.Json, but this endpoint writes its response another way. See GH-4742");
        }
    }

    /// <summary>
    /// Called from DetermineFrames after everything that reads the body has been placed, the audit frame
    /// included, so the buffering is emitted ahead of all of them.
    /// </summary>
    private void bufferRequestForDeduplicatedWithResponse()
    {
        if (DeduplicatedWithResponse != null && !Middleware.OfType<EnableRequestBufferingFrame>().Any())
        {
            Middleware.Insert(0, new EnableRequestBufferingFrame());
        }
    }
}
