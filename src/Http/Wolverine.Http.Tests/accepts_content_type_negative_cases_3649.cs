using System.Net;
using System.Net.Http.Headers;
using System.Text;
using Microsoft.Extensions.DependencyInjection;
using Shouldly;
using Xunit;

namespace Wolverine.Http.Tests;

/// <summary>
///     GH-3649. <c>[AcceptsContentType]</c> had only happy-path coverage
///     (<see cref="content_negotiation_by_content_type" />: match v1, match v2, case-insensitive), while the
///     metadata it emits is acted on by TWO matcher policies — ASP.NET Core's <c>AcceptsMatcherPolicy</c>
///     (an <c>INodeBuilderPolicy</c>, which compiles content types into route-matcher edges and supplies a 415)
///     and Wolverine's own <see cref="ContentTypeEndpointSelectorPolicy" /> (an <c>IEndpointSelectorPolicy</c>,
///     which invalidates candidates after selection). Nothing pinned what the combination actually does.
///
///     <para>These tests pin it. Measured by unregistering Wolverine's policy and re-running every case, the
///     division of labour is:</para>
///     <list type="bullet">
///       <item>every <em>mismatched</em> Content-Type is handled by the framework policy alone → 415. Wolverine's
///       policy is redundant for these.</item>
///       <item>a <em>missing</em> Content-Type is the one case where Wolverine's policy is load-bearing: without
///       it both same-route endpoints stay valid candidates and routing throws an ambiguous match (500).</item>
///     </list>
///
///     <para>The missing-Content-Type case originally answered a bare <b>404</b> — the policy invalidated every
///     candidate and supplied nothing in their place. These tests pinned that 404 and left the decision open;
///     it now answers <b>415</b>, matching what the same endpoints have always returned for a Content-Type
///     that is present but undeclared. See <c>a_missing_content_type_is_unsupported_media_type</c>.</para>
/// </summary>
public class accepts_content_type_negative_cases_3649 : IntegrationContext
{
    public accepts_content_type_negative_cases_3649(AppFixture fixture) : base(fixture)
    {
    }

    // Both /content-negotiation/items endpoints declare exactly one vnd media type each, so this exercises
    // content-type dispatch between two endpoints sharing a route.
    private Task<HttpStatusCode> statusFor(string? contentType) => statusFor("/content-negotiation/items", contentType);

    private async Task<HttpStatusCode> statusFor(string url, string? contentType)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, url)
        {
            Content = new StringContent("{\"name\":\"Test Item\"}", Encoding.UTF8)
        };

        // StringContent defaults to text/plain; clear it so "no Content-Type" really means none.
        request.Content.Headers.ContentType = null;

        if (contentType != null)
        {
            if (MediaTypeHeaderValue.TryParse(contentType, out var parsed))
            {
                request.Content.Headers.ContentType = parsed;
            }
            else
            {
                request.Content.Headers.TryAddWithoutValidation("Content-Type", contentType);
            }
        }

        var response = await Host.Server.CreateClient().SendAsync(request);
        return response.StatusCode;
    }

    [Fact]
    public async Task a_declared_content_type_is_accepted()
    {
        (await statusFor("application/vnd.item.v1+json")).ShouldBe(HttpStatusCode.OK);
    }

    [Fact]
    public async Task media_type_parameters_are_ignored_when_matching()
    {
        // IsContentTypeMatch parses through MediaTypeHeaderValue and compares the media type only, so a
        // charset (or any other parameter) must not defeat the match.
        (await statusFor("application/vnd.item.v1+json; charset=utf-8")).ShouldBe(HttpStatusCode.OK);
    }

    [Fact]
    public async Task an_undeclared_content_type_is_rejected_as_unsupported_media_type()
    {
        (await statusFor("application/json")).ShouldBe(HttpStatusCode.UnsupportedMediaType);
        (await statusFor("text/plain")).ShouldBe(HttpStatusCode.UnsupportedMediaType);
    }

    [Fact]
    public async Task a_structured_suffix_does_not_match_its_base_type()
    {
        // The declared types are application/vnd.item.vN+json. Matching is exact equality on the parsed media
        // type, so a plain application/json request does NOT match the +json suffix. Pinned deliberately so
        // nobody "improves" this into suffix-aware matching without deciding to.
        (await statusFor("application/json")).ShouldBe(HttpStatusCode.UnsupportedMediaType);
    }

    [Fact]
    public async Task a_wildcard_request_content_type_does_not_match()
    {
        // */* is meaningless as a request Content-Type (it belongs in Accept), and IsContentTypeMatch is exact
        // equality, so it is rejected rather than matching everything.
        (await statusFor("*/*")).ShouldBe(HttpStatusCode.UnsupportedMediaType);
    }

    [Fact]
    public async Task a_malformed_content_type_is_rejected_as_unsupported_media_type()
    {
        // MediaTypeHeaderValue.TryParse fails, so IsContentTypeMatch returns false.
        (await statusFor("not-a-media-type")).ShouldBe(HttpStatusCode.UnsupportedMediaType);
    }

    /// <summary>
    ///     A request with a body but no <c>Content-Type</c> header used to get a bare <b>404</b>, while the very
    ///     same endpoints answered <b>415</b> for a Content-Type that was present but undeclared. 415 is the
    ///     defensible answer for both, and is now what both give.
    ///
    ///     <para>The original cause was that <see cref="ContentTypeEndpointSelectorPolicy" /> invalidated every
    ///     candidate (<c>SetValidity(i, false)</c>) and supplied nothing in their place, so routing found no
    ///     endpoint and fell through. Note that deleting the <c>string.IsNullOrEmpty</c> branch does NOT fix it
    ///     — verified — because the framework's node-builder policy then leaves both same-route candidates valid
    ///     and the request becomes an ambiguous match (500). The fix therefore <em>supplies</em> a 415 endpoint
    ///     when content-type filtering empties the candidate set, rather than skipping the invalidation.</para>
    /// </summary>
    [Fact]
    public async Task a_missing_content_type_is_unsupported_media_type()
    {
        (await statusFor(null)).ShouldBe(HttpStatusCode.UnsupportedMediaType);
    }

    [Fact]
    public async Task a_sole_endpoint_owning_its_route_agrees_on_every_status()
    {
        // /content-negotiation/sole has no same-route sibling competing for candidacy, so the framework policy
        // resolves mismatches on its own and Wolverine's only contributes the missing-header case. The point is
        // that the answer does not depend on which shape you happen to have.
        (await statusFor("/content-negotiation/sole", "application/vnd.sole.v1+json")).ShouldBe(HttpStatusCode.OK);
        (await statusFor("/content-negotiation/sole", "application/json")).ShouldBe(HttpStatusCode.UnsupportedMediaType);
        (await statusFor("/content-negotiation/sole", null)).ShouldBe(HttpStatusCode.UnsupportedMediaType);
    }

    [Fact]
    public async Task a_method_mismatch_is_still_405_not_415()
    {
        // GET /content-negotiation/items carries no Content-Type, so the 415 substitution must not swallow the
        // 405 that HttpMethodMatcherPolicy (Order 0) has already decided on. The policy only records candidates
        // that were valid when it saw them, which is what keeps these two apart.
        var response = await Host.Server.CreateClient().GetAsync("/content-negotiation/items");
        response.StatusCode.ShouldBe(HttpStatusCode.MethodNotAllowed);
    }

    [Fact]
    public async Task an_unmatched_route_is_still_404()
    {
        // The 415 is scoped to routes that exist and declare content types. Nothing about supplying it should
        // make a genuinely unknown path look like a media-type problem.
        (await statusFor("/content-negotiation/no-such-route", "application/vnd.item.v1+json"))
            .ShouldBe(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task wolverine_s_selector_policy_is_still_registered()
    {
        // The measurements above only hold while both policies are in play. If someone concludes the Wolverine
        // policy is redundant and removes it, the missing-Content-Type case regresses from its 415 to a 500
        // ambiguous match — so this asserts the policy is present and points at why.
        Host.Services.GetServices<Microsoft.AspNetCore.Routing.MatcherPolicy>()
            .OfType<ContentTypeEndpointSelectorPolicy>()
            .ShouldNotBeEmpty(
                "Removing this policy turns a missing Content-Type into a 500 ambiguous match. See GH-3649.");
    }
}
