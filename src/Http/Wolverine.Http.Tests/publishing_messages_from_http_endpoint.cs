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
}