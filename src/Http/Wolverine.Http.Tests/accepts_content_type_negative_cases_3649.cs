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
///       it both same-route endpoints stay valid candidates and routing throws an ambiguous match (500).
///       With it, all candidates are invalidated → 404.</item>
///     </list>
///
///     <para>That 404 is the one outcome here that looks wrong — see
///     <c>a_missing_content_type_currently_404s</c> below. It is pinned rather than asserted-as-correct.</para>
/// </summary>
public class accepts_content_type_negative_cases_3649 : IntegrationContext
{
    public accepts_content_type_negative_cases_3649(AppFixture fixture) : base(fixture)
    {
    }

    // Both /content-negotiation/items endpoints declare exactly one vnd media type each, so this exercises
    // content-type dispatch between two endpoints sharing a route.
    private async Task<HttpStatusCode> statusFor(string? contentType)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, "/content-negotiation/items")
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
    ///     PINNING CURRENT BEHAVIOUR, NOT ENDORSING IT. A request with a body but no <c>Content-Type</c> header
    ///     gets a bare <b>404</b>, while the very same endpoints answer <b>415</b> for a Content-Type that is
    ///     present but undeclared. 415 is the defensible answer for both.
    ///
    ///     <para>Cause: <see cref="ContentTypeEndpointSelectorPolicy" /> invalidates every candidate
    ///     (<c>SetValidity(i, false)</c>) with no 415 fallback, so routing finds nothing and falls through.
    ///     Removing just that <c>string.IsNullOrEmpty</c> branch does NOT fix it — verified — because the
    ///     framework's node-builder policy then leaves both same-route candidates valid and the request becomes
    ///     an ambiguous match (500). So fixing this properly means supplying a 415 result rather than deleting
    ///     the invalidation.</para>
    ///
    ///     <para>Change this assertion to <c>UnsupportedMediaType</c> when that fix lands. It is a
    ///     user-visible behaviour change, which is why it is not bundled here.</para>
    /// </summary>
    [Fact]
    public async Task a_missing_content_type_currently_404s()
    {
        (await statusFor(null)).ShouldBe(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task wolverine_s_selector_policy_is_still_registered()
    {
        // The measurements above only hold while both policies are in play. If someone concludes the Wolverine
        // policy is redundant and removes it, the missing-Content-Type case regresses from 404 to a 500
        // ambiguous match — so this asserts the policy is present and points at why.
        Host.Services.GetServices<Microsoft.AspNetCore.Routing.MatcherPolicy>()
            .OfType<ContentTypeEndpointSelectorPolicy>()
            .ShouldNotBeEmpty(
                "Removing this policy turns a missing Content-Type into a 500 ambiguous match. See GH-3649.");
    }
}
