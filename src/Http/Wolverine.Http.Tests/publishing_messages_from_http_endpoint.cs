using Microsoft.AspNetCore.Http.Metadata;
using Microsoft.AspNetCore.Routing;
using Shouldly;
using WolverineWebApi;

namespace Wolverine.Http.Tests;

public class publishing_messages_from_http_endpoint : IntegrationContext
{
    public publishing_messages_from_http_endpoint(AppFixture fixture) : base(fixture)
    {
    }

    [Fact]
    public async Task publish_directly()
    {
        var (tracked, result) = await TrackedHttpCall(x =>
        {
            x.Post.Json(new HttpMessage1("Glenn Frey")).ToUrl("/publish/message1");
            x.StatusCodeShouldBe(202);
        });

        tracked.Sent.SingleMessage<HttpMessage1>()
            .Name.ShouldBe("Glenn Frey");
    }

    [Fact]
    public void endpoint_should_not_produce_status_code_200()
    {
        var endpoint = EndpointFor("/publish/message1");

        // Status-code 202 should be added in the PublishingEndpoint
        endpoint.Metadata.FirstOrDefault(x => x is IProducesResponseTypeMetadata m && m.StatusCode == 202).ShouldNotBeNull();

        // And status-code 200 is removed
        endpoint.Metadata.FirstOrDefault(x => x is IProducesResponseTypeMetadata m && m.StatusCode == 200).ShouldBeNull();
    }

    [Fact]
    public void endpoint_describes_the_message_it_reads_from_the_body()
    {
        // GH-3646: these endpoints are built through HttpGraph.Add rather than from a [WolverinePost]
        // attribute. The route used to be mapped onto the chain AFTER the constructor had already applied
        // metadata, so the endpoint advertised no request body at all and carried no HTTP method.
        var endpoint = EndpointFor("/publish/message1");

        var accepts = endpoint.Metadata.OfType<IAcceptsMetadata>().ShouldHaveSingleItem();
        accepts.RequestType.ShouldBe(typeof(HttpMessage1));
        accepts.ContentTypes.ShouldContain("application/json");

        endpoint.Metadata.OfType<HttpMethodMetadata>()
            .SelectMany(x => x.HttpMethods)
            .ShouldContain("POST");
    }
}