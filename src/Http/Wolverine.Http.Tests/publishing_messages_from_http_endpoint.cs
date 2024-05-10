using Microsoft.AspNetCore.Http.Metadata;
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
}