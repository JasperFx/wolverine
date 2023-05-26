using Shouldly;
using WolverineWebApi;

namespace Wolverine.Http.Tests;

public class mapping_routes_to_wolverine_handlers : IntegrationContext
{
    public mapping_routes_to_wolverine_handlers(AppFixture fixture) : base(fixture)
    {
    }

    [Fact]
    public async Task map_post_to_wolverine_handler()
    {
        var (tracked, result) =
            await TrackedHttpCall(x => { x.Post.Json(new HttpMessage1("one")).ToUrl("/wolverine"); });

        tracked.Executed.SingleMessage<HttpMessage1>()
            .Name.ShouldBe("one");
    }

    [Fact]
    public async Task map_put_to_wolverine_handler()
    {
        var (tracked, result) = await TrackedHttpCall(x =>
        {
            x.Put.Json(new HttpMessage2("two")).ToUrl("/wolverine");
        });

        tracked.Executed.SingleMessage<HttpMessage2>()
            .Name.ShouldBe("two");
    }


    [Fact]
    public async Task map_delete_to_wolverine_handler()
    {
        var (tracked, result) = await TrackedHttpCall(x =>
        {
            x.Delete.Json(new HttpMessage3("three")).ToUrl("/wolverine");
        });

        tracked.Executed.SingleMessage<HttpMessage3>()
            .Name.ShouldBe("three");
    }

    [Fact]
    public async Task map_post_with_request_response()
    {
        var (tracked, result) = await TrackedHttpCall(x =>
        {
            x.Post.Json(new CustomRequest("Alan Alda")).ToUrl("/wolverine/request");
        });

        result.ReadAsJson<CustomResponse>().Name.ShouldBe("Alan Alda");

        tracked.Sent.SingleMessage<CustomResponse>();
    }


    [Fact]
    public async Task map_put_with_request_response()
    {
        var (tracked, result) = await TrackedHttpCall(x =>
        {
            x.Put.Json(new CustomRequest("FDR")).ToUrl("/wolverine/request");
        });

        result.ReadAsJson<CustomResponse>().Name.ShouldBe("FDR");

        tracked.Sent.SingleMessage<CustomResponse>();
    }


    [Fact]
    public async Task map_delete_with_request_response()
    {
        var (tracked, result) = await TrackedHttpCall(x =>
        {
            x.Delete.Json(new CustomRequest("LBJ")).ToUrl("/wolverine/request");
        });

        result.ReadAsJson<CustomResponse>().Name.ShouldBe("LBJ");

        tracked.Sent.SingleMessage<CustomResponse>();
    }
}