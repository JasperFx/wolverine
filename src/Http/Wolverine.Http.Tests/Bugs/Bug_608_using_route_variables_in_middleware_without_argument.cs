using WolverineWebApi.Bugs;

namespace Wolverine.Http.Tests.Bugs;

public class Bug_608_using_route_variables_in_middleware_without_argument : IntegrationContext
{
    public Bug_608_using_route_variables_in_middleware_without_argument(AppFixture fixture) : base(fixture)
    {
    }

    [Fact]
    public async Task can_use_route_argument_in_middleware()
    {
        using var session = Store.LightweightSession();
        session.Store(new SomeDocument{Id = "ball"});
        await session.SaveChangesAsync();

        await Scenario(x =>
        {
            x.Post.Json(new SomeRequest("Basketball")).ToUrl("/some/ball");
            x.StatusCodeShouldBe(204);
        });
    }

    [Fact]
    public async Task using_route_argument_and_getting_404_on_miss_load()
    {
        await Scenario(x =>
        {
            x.Post.Json(new SomeRequest("Basketball")).ToUrl("/some/nonexistent");
            x.StatusCodeShouldBe(404);
        });
    }
}