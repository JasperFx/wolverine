using Alba;
using Shouldly;
using WolverineWebApi;

namespace Wolverine.Http.Tests;

// GH-516 parity for HTTP chains: a Before middleware method may return the request
// type to replace an immutable record request body before the handler runs
public class middleware_replacing_immutable_request : IntegrationContext
{
    public middleware_replacing_immutable_request(AppFixture fixture) : base(fixture)
    {
    }

    [Fact]
    public async Task able_to_use_the_replaced_request()
    {
        var body = await Scenario(x =>
        {
            x.Post.Json(new StampedRequest { Name = "original" }).ToUrl("/middleware/stamped");
            x.StatusCodeShouldBeOk();
        });

        // The sync Before stamped StampedBy, the async Before rewrote Name,
        // and the tuple Before flipped Enriched
        (await body.ReadAsTextAsync()).ShouldBe("original-async:sync:True");
    }

    [Fact]
    public async Task tuple_returning_before_can_still_short_circuit()
    {
        await Scenario(x =>
        {
            x.Post.Json(new StampedRequest { Name = "stop" }).ToUrl("/middleware/stamped");
            x.StatusCodeShouldBe(423);
        });
    }
}
