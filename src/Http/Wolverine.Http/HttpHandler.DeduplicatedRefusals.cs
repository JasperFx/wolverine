using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Wolverine.Http.Runtime;
using Wolverine.Persistence.Durability;

namespace Wolverine.Http;

// GH-5206. A [DeduplicatedWithResponse] chain that claims inside its transaction looks its claim up once, at the front.
// A concurrent repeat can miss that claim, then read the first request's committed work and fail over it -- a
// validation answering 409 "already open", a 404, a throw -- before it ever reaches the commit that
// AnswerDeduplicatedResponseRaceFrame wraps. Its client is then refused for a request that succeeded.
//
// The claim commits in the same transaction as that work, so a repeat that has read the work finds the claim on a
// second lookup. Until this request commits, its response is held back; a request that fails before its commit is
// looked up again, and answered from a claim if there is one.
public abstract partial class HttpHandler
{
    private static readonly object _deduplicatedRefusalWatch = new();

    // Set once a request has been answered from an earlier request's claim, so it is not looked up again
    private static readonly object _deduplicatedRepeatAnswered = new();

    // Set once the commit itself has failed: TryAnswerDeduplicatedCommitFailureAsync has decided that one, and a failure
    // that finds this request's OWN claim committed is rethrown there, not answered from it here
    private static readonly object _deduplicatedCommitFailed = new();

    /// <summary>
    /// GH-5206. Called by the generated code of a <c>[DeduplicatedWithResponse]</c> chain that claims inside its
    /// transaction, once its claim lookup has found nothing: holds back the response until the commit, so a failure
    /// before it can still be answered from a claim that a concurrent request with the same key committed meanwhile.
    /// Does nothing for a request without a key.
    /// </summary>
    public void WatchForDeduplicatedRefusal(HttpContext context, DeduplicatedResponses responses,
        string? deduplicationId, string? fingerprint, string keyName, Type? ancillaryStoreMarker)
    {
        if (string.IsNullOrWhiteSpace(deduplicationId) || context.Response.HasStarted) return;

        // Only under the wrapper that answers from it, which BuildEndpoint puts around every such endpoint
        if (!context.Items.ContainsKey(_deduplicatedRefusalWatch)) return;

        var original = context.Features.Get<IHttpResponseBodyFeature>();
        if (original == null) return;

        var buffer = new MemoryStream();
        var held = new StreamResponseBodyFeature(buffer, original);
        context.Features.Set<IHttpResponseBodyFeature>(held);

        context.Items[_deduplicatedRefusalWatch] = new DeduplicatedRefusalWatch(this, responses, deduplicationId,
            fingerprint, keyName, ancillaryStoreMarker, original, held, buffer);
    }

    /// <summary>
    /// GH-5206. Wraps the endpoint of a <c>[DeduplicatedWithResponse]</c> chain. A chain that claims inside its
    /// transaction arms <see cref="WatchForDeduplicatedRefusal" />; for any other, this only passes the request on.
    /// </summary>
    internal static RequestDelegate RecheckDeduplicatedRefusals(RequestDelegate inner) => async context =>
    {
        // A placeholder, so the generated code knows it is wrapped
        context.Items[_deduplicatedRefusalWatch] = null;

        try
        {
            await inner(context).ConfigureAwait(false);
        }
        catch (Exception e) when (context.Items[_deduplicatedRefusalWatch] is DeduplicatedRefusalWatch)
        {
            if (await finishDeduplicatedRefusalWatchAsync(context, e).ConfigureAwait(false)) return;
            throw;
        }

        await finishDeduplicatedRefusalWatchAsync(context, null).ConfigureAwait(false);
    };

    /// <summary>Straight after the commit: from here on, nothing this request answers is over another's work.</summary>
    private static void releaseDeduplicatedRefusalWatch(HttpContext context)
    {
        // Anything written before the commit stays held, and goes out first, at the end
        if (context.Items.TryGetValue(_deduplicatedRefusalWatch, out var raw) &&
            raw is DeduplicatedRefusalWatch { Buffer.Length: 0 } watch)
        {
            context.Items.Remove(_deduplicatedRefusalWatch);
            context.Features.Set(watch.Original);
        }
    }

    /// <returns>True when the failure was answered from a claim instead, so it is not rethrown.</returns>
    private static async Task<bool> finishDeduplicatedRefusalWatchAsync(HttpContext context, Exception? failure)
    {
        if (!context.Items.TryGetValue(_deduplicatedRefusalWatch, out var raw) ||
            raw is not DeduplicatedRefusalWatch watch)
        {
            return false;
        }

        context.Items.Remove(_deduplicatedRefusalWatch);

        // Lands whatever the writer has not yet flushed in the buffer
        await watch.Held.CompleteAsync().ConfigureAwait(false);
        context.Features.Set(watch.Original);

        if (isRefusedBeforeItsCommit(context, failure) &&
            await tryAnswerRefusalFromClaimAsync(context, watch, failure).ConfigureAwait(false))
        {
            return true;
        }

        // A throw goes on to whatever handles it, with nothing written
        if (failure == null && watch.Buffer.Length > 0)
        {
            watch.Buffer.Position = 0;
            await watch.Buffer.CopyToAsync(context.Response.Body, context.RequestAborted).ConfigureAwait(false);
        }

        return false;
    }

    private static bool isRefusedBeforeItsCommit(HttpContext context, Exception? failure)
    {
        if (IsDeduplicatedResponseCommitted(context)) return false;
        if (context.Items.ContainsKey(_deduplicatedRepeatAnswered)) return false;
        if (context.Items.ContainsKey(_deduplicatedCommitFailed)) return false;
        if (context.RequestAborted.IsCancellationRequested || context.Response.HasStarted) return false;

        // 400 and up, as CompleteDeduplicatedRequest decides what is a failure
        return failure != null || context.Response.StatusCode >= StatusCodes.Status400BadRequest;
    }

    private static async Task<bool> tryAnswerRefusalFromClaimAsync(HttpContext context,
        DeduplicatedRefusalWatch watch, Exception? failure)
    {
        var logger = context.RequestServices.GetService<ILogger<DeduplicatedResponses>>();

        DeduplicatedResponseClaim? claim;
        try
        {
            claim = await watch.Responses.FindAsync(watch.DeduplicationId, watch.AncillaryStoreMarker)
                .ConfigureAwait(false);
        }
        catch (Exception e)
        {
            // Never mask the endpoint's own answer, or its failure
            logger?.LogError(e,
                "Failed to read the claim on deduplicated id '{DeduplicationId}' after the request failed before its commit",
                watch.DeduplicationId);
            return false;
        }

        if (claim == null) return false;

        logger?.LogInformation(failure,
            "A concurrent request committed deduplicated id '{DeduplicationId}' first; this request failed before its own commit with {StatusCode}, and is answered from that request's claim",
            watch.DeduplicationId, failure == null ? context.Response.StatusCode : "an exception");

        // As TryAnswerDeduplicatedCommitFailureAsync: nothing this request set for its own answer survives
        context.Response.Clear();
        await watch.Handler.AnswerDeduplicatedRepeatAsync(context, claim, watch.Fingerprint, watch.KeyName)
            .ConfigureAwait(false);

        return true;
    }

    private sealed record DeduplicatedRefusalWatch(
        HttpHandler Handler,
        DeduplicatedResponses Responses,
        string DeduplicationId,
        string? Fingerprint,
        string KeyName,
        Type? AncillaryStoreMarker,
        IHttpResponseBodyFeature Original,
        StreamResponseBodyFeature Held,
        MemoryStream Buffer);
}
