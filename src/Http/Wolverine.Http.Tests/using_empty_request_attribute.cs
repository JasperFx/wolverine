using WolverineWebApi;

namespace Wolverine.Http.Tests;

public class using_empty_request_attribute : IntegrationContext
{
    public using_empty_request_attribute(AppFixture fixture) : base(fixture)
    {
    }

    [Fact]
    public async Task using_empty_request()
    {
        await Scenario(x =>
        {
            x.Delete.Json(new UserId(Guid.NewGuid())).ToUrl("/api/trainer");
            x.StatusCodeShouldBe(400); // test was borrowed from a bug report
        });
    }
}