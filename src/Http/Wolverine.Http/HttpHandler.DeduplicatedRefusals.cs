using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Wolverine.Http.Runtime;
using Wolverine.Persistence;
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
//
// [Deduplicated] on a chain whose claim rides its own transaction (Marten, Polecat, Fisher) has the same race, and
// the same answer: a repeat refused over the first request's work is refused as the duplicate it is instead.
public abstract partial class HttpHandler
{
    private static readonly object _deduplicatedRefusalWatch = new();

    // Set once a request has been answered from an earlier request's claim, so it is not looked up again
    private static readonly object _deduplicatedRepeatAnswered = new();

    // Set once the commit itself has failed: TryAnswerDeduplicatedCommitFailureAsync has decided that one, and a failure
    // that finds this request's OWN claim committed is rethrown there, not answered from it here
    private static readonly object _deduplicatedCommitFailed = new();

    // Set just before a [Deduplicated] chain's commit: a failure from there on is the commit race wrapper's to answer
    private static readonly object _deduplicationCommitStarted = new();

    /// <summary>
    /// GH-5206. Called by the generated code of a <c>[DeduplicatedWithResponse]</c> chain that claims inside its
    /// transaction, once its claim lookup has found nothing: holds back the response until the commit, so a failure
    /// before it can still be answered from a claim that a concurrent request with the same key committed meanwhile.
    /// Does nothing for a request without a key.
    /// </summary>
    public void WatchForDeduplicatedRefusal(HttpContext context, DeduplicatedResponses responses,
        string? deduplicationId, string? fingerprint, string keyName, Type? ancillaryStoreMarker)
    {
        if (string.IsNullOrWhiteSpace(deduplicationId)) return;

        holdResponseUntilCommit(context,
            new ReplayFromClaim(this, responses, deduplicationId, fingerprint, keyName, ancillaryStoreMarker));
    }

    /// <summary>
    /// GH-5206. As <see cref="WatchForDeduplicatedRefusal" />, for a <c>[Deduplicated]</c> chain whose claim rides its
    /// own transaction: a failure before the commit, when a concurrent request with the same id committed meanwhile,
    /// is answered as a duplicate. <paramref name="detail" /> is the problem detail, or null for a success status
    /// that is written without one. Does nothing without an id, or with a custom deduplicator or a store that cannot
    /// look a claim up.
    /// </summary>
    public void WatchForDuplicateRefusal(HttpContext context, IMessageDeduplicator deduplicator,
        string? deduplicationId, Type? ancillaryStoreMarker, int statusCode, string? detail)
    {
        if (string.IsNullOrWhiteSpace(deduplicationId) || deduplicator is not MessageDeduplicator builtIn) return;

        holdResponseUntilCommit(context,
            new RefuseAsDuplicate(this, builtIn, deduplicationId, ancillaryStoreMarker, statusCode, detail));
    }

    /// <summary>GH-5206. Just before a <c>[Deduplicated]</c> chain commits the transaction its claim rides.</summary>
    public static void MarkDeduplicationCommitStarted(HttpContext context)
    {
        context.Items[_deduplicationCommitStarted] = true;
        releaseDeduplicatedRefusalWatch(context);
    }

    private static void holdResponseUntilCommit(HttpContext context, RefusalAnswer answer)
    {
        if (context.Response.HasStarted) return;

        // Only under the wrapper that answers from it, which BuildEndpoint puts around every such endpoint
        if (!context.Items.ContainsKey(_deduplicatedRefusalWatch)) return;

        var original = context.Features.Get<IHttpResponseBodyFeature>();
        if (original == null) return;

        var buffer = new MemoryStream();
        var held = new StreamResponseBodyFeature(buffer, original);
        context.Features.Set<IHttpResponseBodyFeature>(held);

        context.Items[_deduplicatedRefusalWatch] = new DeduplicatedRefusalWatch(answer, original, held, buffer);
    }

    /// <summary>
    /// GH-5206. Wraps the endpoint of a <c>[DeduplicatedWithResponse]</c> or <c>[Deduplicated]</c> chain. A chain that
    /// claims inside its transaction arms <see cref="WatchForDeduplicatedRefusal" /> or
    /// <see cref="WatchForDuplicateRefusal" />; for any other, this only passes the request on.
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
            await watch.Answer.TryAnswerAsync(context, failure).ConfigureAwait(false))
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
        if (context.Items.ContainsKey(_deduplicationCommitStarted)) return false;
        if (context.RequestAborted.IsCancellationRequested || context.Response.HasStarted) return false;

        // 400 and up, as CompleteDeduplicatedRequest decides what is a failure
        return failure != null || context.Response.StatusCode >= StatusCodes.Status400BadRequest;
    }

    private static string describeFailure(HttpContext context, Exception? failure)
        => failure == null ? context.Response.StatusCode.ToString() : "an exception";

    /// <summary>How a request that failed before its commit is answered, when another request committed its id.</summary>
    private abstract record RefusalAnswer(string DeduplicationId)
    {
        /// <returns>True when answered, so the request's own answer or failure is dropped.</returns>
        public async Task<bool> TryAnswerAsync(HttpContext context, Exception? failure)
        {
            var logger = context.RequestServices.GetService<ILogger<DeduplicatedResponses>>();

            try
            {
                if (!await isClaimedAsync().ConfigureAwait(false)) return false;
            }
            catch (Exception e)
            {
                // Never mask the endpoint's own answer, or its failure
                logger?.LogError(e,
                    "Failed to read the claim on deduplicated id '{DeduplicationId}' after the request failed before its commit",
                    DeduplicationId);
                return false;
            }

            logger?.LogInformation(failure,
                "A concurrent request committed deduplicated id '{DeduplicationId}' first; this request failed before its own commit with {Failure}, and is answered as a repeat",
                DeduplicationId, describeFailure(context, failure));

            // As TryAnswerDeduplicatedCommitFailureAsync: nothing this request set for its own answer survives
            context.Response.Clear();
            await answerAsync(context).ConfigureAwait(false);

            return true;
        }

        protected abstract Task<bool> isClaimedAsync();

        protected abstract Task answerAsync(HttpContext context);
    }

    // [DeduplicatedWithResponse]: the stored response, or 422 for a different request
    private sealed record ReplayFromClaim(
        HttpHandler Handler,
        DeduplicatedResponses Responses,
        string DeduplicationId,
        string? Fingerprint,
        string KeyName,
        Type? AncillaryStoreMarker) : RefusalAnswer(DeduplicationId)
    {
        private DeduplicatedResponseClaim? _claim;

        protected override async Task<bool> isClaimedAsync()
        {
            _claim = await Responses.FindAsync(DeduplicationId, AncillaryStoreMarker).ConfigureAwait(false);
            return _claim != null;
        }

        protected override Task answerAsync(HttpContext context)
            => Handler.AnswerDeduplicatedRepeatAsync(context, _claim!, Fingerprint, KeyName);
    }

    // [Deduplicated]: the endpoint's duplicate answer, as its up-front check gives it
    private sealed record RefuseAsDuplicate(
        HttpHandler Handler,
        MessageDeduplicator Deduplicator,
        string DeduplicationId,
        Type? AncillaryStoreMarker,
        int StatusCode,
        string? Detail) : RefusalAnswer(DeduplicationId)
    {
        protected override async Task<bool> isClaimedAsync()
            => await Deduplicator.IsClaimedAsync(DeduplicationId, AncillaryStoreMarker, CancellationToken.None)
                .ConfigureAwait(false) == true;

        protected override Task answerAsync(HttpContext context)
        {
            if (Detail == null)
            {
                context.Response.StatusCode = StatusCode;
                return Task.CompletedTask;
            }

            return Handler.WriteProblems(StatusCode, Detail, context, null);
        }
    }

    private sealed record DeduplicatedRefusalWatch(
        RefusalAnswer Answer,
        IHttpResponseBodyFeature Original,
        StreamResponseBodyFeature Held,
        MemoryStream Buffer);
}
