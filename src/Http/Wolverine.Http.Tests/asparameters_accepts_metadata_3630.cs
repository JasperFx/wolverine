using Microsoft.AspNetCore.Http.Metadata;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Shouldly;

namespace Wolverine.Http.Tests;

/// <summary>
///     GH-3630 (continuation of GH-3591). Every <c>[AsParameters]</c> chain was marked
///     <c>IsFormData = true</c> the moment the parameter was matched — before any of its members had been
///     walked — and only a <c>[FromBody]</c> member ever undid that. So an <c>[AsParameters]</c> type bound
///     purely from the query string got <c>Accepts("application/x-www-form-urlencoded", "multipart/form-data")</c>
///     stamped onto its endpoint, on a GET.
///
///     <para>ASP.NET Core does not treat that as merely cosmetic: <c>AcceptsMatcherPolicy</c> is a node-builder
///     policy that turns <c>IAcceptsMetadata</c> into content-type edges in the route matcher. Once anything
///     else in the app contributed a route pattern of the same shape — <c>MapGrpcService</c>'s
///     <c>{unimplementedService}/{unimplementedMethod}</c> catch-all in the reported case — a plain GET
///     carrying no Content-Type stopped being a candidate for its own route and returned <b>404</b>, not 415.
///     Hence "HttpEndpoint gives 404 with GrpcServices": the gRPC mapping only exposed metadata that was
///     already wrong.</para>
/// </summary>
public class asparameters_accepts_metadata_3630 : IntegrationContext
{
    public asparameters_accepts_metadata_3630(AppFixture fixture) : base(fixture)
    {
    }

    private IAcceptsMetadata? acceptsFor(string route)
    {
        var endpoint = Host.Services.GetServices<EndpointDataSource>()
            .SelectMany(x => x.Endpoints)
            .OfType<RouteEndpoint>()
            .Single(x => x.RoutePattern.RawText == route);

        return endpoint.Metadata.OfType<IAcceptsMetadata>().FirstOrDefault();
    }

    [Fact]
    public void a_query_only_asparameters_get_declares_no_request_body()
    {
        // /api/asparameters/aliased-array is GET with [AsParameters] AliasedArrayQuery, whose only member
        // is [FromQuery]. It reads no body, so it must advertise none.
        acceptsFor("/api/asparameters/aliased-array").ShouldBeNull(
            "a GET bound entirely from the query string must not advertise that it accepts a request body");
    }

    [Fact]
    public void a_query_only_asparameters_get_does_not_advertise_form_content_types()
    {
        // The specific stamped value from the bug, spelled out so a regression is unambiguous.
        var accepts = acceptsFor("/api/asparameters/aliased-int-array");

        (accepts?.ContentTypes ?? []).ShouldNotContain("application/x-www-form-urlencoded");
        (accepts?.ContentTypes ?? []).ShouldNotContain("multipart/form-data");
    }

    [Fact]
    public void an_asparameters_type_with_form_members_still_declares_form_content_types()
    {
        // The guard against over-correcting: /api/asparameters1 is a POST whose [AsParameters] type does
        // have [FromForm] members, so its form Accepts metadata is correct and must survive.
        var accepts = acceptsFor("/api/asparameters1");

        accepts.ShouldNotBeNull();
        accepts!.ContentTypes.ShouldContain("application/x-www-form-urlencoded");
        accepts.ContentTypes.ShouldContain("multipart/form-data");
    }

    [Fact]
    public async Task a_query_only_asparameters_get_still_binds_and_responds()
    {
        // End to end, with no Content-Type on the request at all.
        var result = await Host.Scenario(x => x
            .Get
            .Url("/api/asparameters/aliased-array?v=red&v=blue"));

        var query = await result.ReadAsJsonAsync<WolverineWebApi.AliasedArrayQuery>();
        query!.Values.ShouldBe(["red", "blue"]);
    }
}
