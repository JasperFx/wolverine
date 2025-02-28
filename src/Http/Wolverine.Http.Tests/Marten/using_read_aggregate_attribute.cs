using System.Diagnostics;
using Alba;
using Marten;
using Shouldly;
using WolverineWebApi.Marten;

namespace Wolverine.Http.Tests.Marten;

public class using_read_aggregate_attribute(AppFixture fixture) : IntegrationContext(fixture)
{
    [Fact]
    public async Task happy_path_reading_aggregate()
    {
        var id = Guid.NewGuid();

        // Creating a new order
        await Scenario(x =>
        {
            x.Post.Json(new StartOrderWithId(id, ["Socks", "Shoes", "Shirt"])).ToUrl("/orders/create4");
        });

        var result = await Host.GetAsJson<Order>("/orders/latest/" + id);
        result.Items.Keys.ShouldContain("Socks");
    }

    [Fact]
    public async Task sad_path_no_aggregate_return_404()
    {
        await Scenario(x =>
        {
            x.Get.Url("/orders/latest/" + Guid.NewGuid());
            x.StatusCodeShouldBe(404);
        });
    }
}