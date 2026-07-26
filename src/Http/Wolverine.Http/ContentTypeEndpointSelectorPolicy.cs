using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.AspNetCore.Routing.Matching;
using Microsoft.Net.Http.Headers;

namespace Wolverine.Http;

/// <summary>
/// ASP.NET Core MatcherPolicy that filters candidate endpoints by the request's Content-Type
/// header when endpoints are decorated with <see cref="AcceptsContentTypeAttribute"/>.
/// </summary>
/// <remarks>
/// GH-3649 measured what this policy actually contributes on top of the framework's own
/// <c>AcceptsMatcherPolicy</c>, which acts on the same <c>IAcceptsMetadata</c>. For every request whose
/// Content-Type is <em>present but undeclared</em> the framework policy already resolves the mismatch to a
/// 415 at node-build time, so this policy is redundant there. It is load-bearing for exactly one case: a
/// request with <em>no Content-Type header at all</em>, where the framework leaves every same-route
/// candidate valid and routing throws an ambiguous match. Invalidating those candidates is therefore
/// necessary — but invalidating them and stopping leaves nothing for the router to select, which surfaced
/// as a bare 404 while the identical endpoints answered 415 for a header that was merely wrong. So when
/// content-type filtering empties the candidate set, this policy now supplies the same 415 the framework
/// would have.
/// </remarks>
internal class ContentTypeEndpointSelectorPolicy : MatcherPolicy, IEndpointSelectorPolicy
{
    // Mirrors the framework's own AcceptsMatcherPolicy, which substitutes an equivalent endpoint when its
    // content-type edges resolve to no match.
    private static readonly Endpoint _unsupportedMediaTypeEndpoint = new(
        context =>
        {
            context.Response.StatusCode = StatusCodes.Status415UnsupportedMediaType;
            return Task.CompletedTask;
        },
        EndpointMetadataCollection.Empty,
        "415 HTTP Unsupported Media Type");

    // Run after HttpMethodMatcherPolicy (order 0) and other built-in policies
    public override int Order => 100;

    public bool AppliesToEndpoints(IReadOnlyList<Endpoint> endpoints)
    {
        for (var i = 0; i < endpoints.Count; i++)
        {
            if (endpoints[i].Metadata.GetMetadata<AcceptsContentTypeAttribute>() != null)
            {
                return true;
            }
        }

        return false;
    }

    public Task ApplyAsync(HttpContext httpContext, CandidateSet candidates)
    {
        var requestContentType = httpContext.Request.ContentType;

        // Index of a candidate this policy rejected on content type, kept so we can hand the router a 415 if
        // it turns out we rejected all of them. Only candidates that were valid coming in are recorded, so a
        // candidate already invalidated by an earlier policy — a method mismatch, say — never gets its 405
        // turned into a 415.
        var rejected = -1;
        var anyValid = false;

        for (var i = 0; i < candidates.Count; i++)
        {
            if (!candidates.IsValidCandidate(i))
            {
                continue;
            }

            var endpoint = candidates[i].Endpoint;
            var acceptsAttribute = endpoint?.Metadata.GetMetadata<AcceptsContentTypeAttribute>();

            if (acceptsAttribute == null)
            {
                // No attribute — leave this candidate valid as a fallback
                anyValid = true;
                continue;
            }

            if (string.IsNullOrEmpty(requestContentType))
            {
                // No Content-Type header but endpoint requires specific content type
                candidates.SetValidity(i, false);
                rejected = i;
                continue;
            }

            if (!IsContentTypeMatch(requestContentType, acceptsAttribute.ContentTypes))
            {
                candidates.SetValidity(i, false);
                rejected = i;
            }
            else
            {
                anyValid = true;
            }
        }

        if (!anyValid && rejected >= 0)
        {
            // Every remaining candidate was rejected on content type. Substitute a 415 rather than letting
            // routing fall through to a 404 — the request reached a route that exists and named a media type
            // it does not serve.
            candidates.ReplaceEndpoint(rejected, _unsupportedMediaTypeEndpoint, candidates[rejected].Values);

            // ReplaceEndpoint preserves the candidate's existing score and only ever invalidates (when the
            // replacement endpoint is null) — it does not re-validate. Without this the substituted endpoint is
            // installed on a candidate the loop above already marked invalid, and the router still sees nothing
            // to select.
            candidates.SetValidity(rejected, true);
        }

        return Task.CompletedTask;
    }

    internal static bool IsContentTypeMatch(string requestContentType, string[] acceptedContentTypes)
    {
        // Parse the request content type to get the media type without parameters (charset, etc.)
        if (MediaTypeHeaderValue.TryParse(requestContentType, out var parsedRequestType))
        {
            var requestMediaType = parsedRequestType.MediaType.Value;

            for (var i = 0; i < acceptedContentTypes.Length; i++)
            {
                if (string.Equals(requestMediaType, acceptedContentTypes[i], StringComparison.OrdinalIgnoreCase))
                {
                    return true;
                }
            }
        }

        return false;
    }
}
