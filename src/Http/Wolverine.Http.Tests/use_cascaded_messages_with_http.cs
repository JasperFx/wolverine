using Shouldly;
using WolverineWebApi;

namespace Wolverine.Http.Tests;

public class use_cascaded_messages_with_http : IntegrationContext
{
    public use_cascaded_messages_with_http(AppFixture fixture) : base(fixture)
    {
    }

    [Fact]
    public async Task send_cascaded_messages_from_tuple_response()
    {
        var (tracked, result) = await TrackedHttpCall(x =>
        {
            x.Post.Json(new SpawnInput("Chris Jones")).ToUrl("/spawn");
        });
        
        result.ReadAsText().ShouldBe("got it");
        
        tracked.Sent.SingleMessage<HttpMessage1>().Name.ShouldBe("Chris Jones");
        tracked.Sent.SingleMessage<HttpMessage2>().Name.ShouldBe("Chris Jones");
        tracked.Sent.SingleMessage<HttpMessage3>().Name.ShouldBe("Chris Jones");
        tracked.Sent.SingleMessage<HttpMessage4>().Name.ShouldBe("Chris Jones");
    }

    [Fact]
    public async Task no_content_chains_should_use_cascading_messages_for_create_variables()
    {
        var (tracked, result) = await TrackedHttpCall(x =>
        {
            x.Post.Url("/spawn2");
        });

        tracked.Sent.SingleMessage<HttpMessage1>().ShouldNotBeNull();
        tracked.Sent.SingleMessage<HttpMessage2>().ShouldNotBeNull();
    }
}