using System.Net;
using System.Net.Http.Json;
using Microsoft.AspNetCore.Routing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Shouldly;
using Swashbuckle.AspNetCore.Swagger;
using WolverineWebApi;
using Xunit;

namespace Wolverine.Http.Tests;

public class query_verb_support : IntegrationContext
{
    public query_verb_support(AppFixture fixture) : base(fixture)
    {
    }

    [Fact]
    public async Task query_endpoint_reads_request_body_and_returns_result()
    {
        // QUERY carries a request body (unlike GET). Alba's scenario helpers assume standard verbs,
        // so drive a genuine QUERY request through the test server's HttpClient.
        var client = Host.GetTestServer().CreateClient();

        var request = new HttpRequestMessage(new HttpMethod("QUERY"), "/search")
        {
            Content = JsonContent.Create(new SearchRequest("widget", 3))
        };

        var response = await client.SendAsync(request);
        response.StatusCode.ShouldBe(HttpStatusCode.OK);

        var results = await response.Content.ReadFromJsonAsync<SearchResults>();
        results.ShouldNotBeNull();
        results.Term.ShouldBe("widget");
        results.Page.ShouldBe(3);
        results.Hits.ShouldBe(["widget-1", "widget-2", "widget-3"]);
    }

    [Fact]
    public void query_route_is_registered_with_QUERY_method_metadata()
    {
        var endpoint = EndpointFor("/search");
        var methods = endpoint.Metadata.GetMetadata<HttpMethodMetadata>();
        methods.ShouldNotBeNull();
        methods.HttpMethods.ShouldContain("QUERY");
    }

    [Fact]
    public void query_endpoint_is_not_wrapped_in_transactional_middleware()
    {
        // QUERY is safe, so it must not be enrolled in transactional/outbox middleware. Wolverine's
        // rule is dependency-based (no IMessageBus/MessageContext dependency => no outbox), so a plain
        // QUERY search endpoint stays non-transactional even with AutoApplyTransactions turned on.
        var chain = HttpChains.Chains.Single(x => x.RoutePattern!.RawText == "/search");
        chain.RequiresOutbox().ShouldBeFalse();
        chain.IsTransactional.ShouldBeFalse();
    }

    [Fact]
    public void swagger_generation_still_succeeds_with_a_query_endpoint()
    {
        // A QUERY endpoint must not break OpenAPI generation for the rest of the app. OpenAPI 3.1
        // (what Swashbuckle emits here) has no QUERY operation, so the endpoint is gracefully omitted
        // from the document rather than throwing.
        var generator = Host.Services.GetRequiredService<ISwaggerProvider>();
        var doc = generator.GetSwagger("default");

        doc.Paths.ContainsKey("/search").ShouldBeFalse();
    }
}
