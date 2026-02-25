using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.AspNetCore.Routing.Matching;
using Microsoft.Net.Http.Headers;

namespace Wolverine.Http;

/// <summary>
/// ASP.NET Core MatcherPolicy that filters candidate endpoints by the request's Content-Type
/// header when endpoints are decorated with <see cref="AcceptsContentTypeAttribute"/>.
/// </summary>
internal class ContentTypeEndpointSelectorPolicy : MatcherPolicy, IEndpointSelectorPolicy
{
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
                continue;
            }

            if (string.IsNullOrEmpty(requestContentType))
            {
                // No Content-Type header but endpoint requires specific content type
                candidates.SetValidity(i, false);
                continue;
            }

            if (!IsContentTypeMatch(requestContentType, acceptsAttribute.ContentTypes))
            {
                candidates.SetValidity(i, false);
            }
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
