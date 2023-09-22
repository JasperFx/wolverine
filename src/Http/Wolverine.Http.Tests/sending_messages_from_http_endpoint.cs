using Shouldly;
using WolverineWebApi;

namespace Wolverine.Http.Tests;

public class sending_messages_from_http_endpoint : IntegrationContext
{
    public sending_messages_from_http_endpoint(AppFixture fixture) : base(fixture)
    {
    }

    [Fact]
    public async Task send_directly()
    {
        var (tracked, result) = await TrackedHttpCall(x =>
        {
            x.Post.Json(new HttpMessage5("Glenn Frey")).ToUrl("/send/message5");
            x.StatusCodeShouldBe(202);
        });

        tracked.Sent.SingleMessage<HttpMessage5>()
            .Name.ShouldBe("Glenn Frey");
    }
}