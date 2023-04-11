using WolverineWebApi;

namespace Wolverine.Http.Tests;

public class variable_status_code_return_values : IntegrationContext
{
    public variable_status_code_return_values(AppFixture fixture) : base(fixture)
    {
    }

    [Fact]
    public async Task set_status_code_from_int_return_value()
    {
        await Scenario(x =>
        {
            x.Post.Json(new StatusCodeRequest(202)).ToUrl("/status");
            x.StatusCodeShouldBe(202);
        });
        
        await Scenario(x =>
        {
            x.Post.Json(new StatusCodeRequest(205)).ToUrl("/status");
            x.StatusCodeShouldBe(205);
        });
    }
}