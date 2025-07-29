using Alba;
using Microsoft.AspNetCore.Mvc;
using Shouldly;
using WolverineWebApi;

namespace Wolverine.Http.Tests;

public class combo_handler_and_endpoint : IntegrationContext
{
    public combo_handler_and_endpoint(AppFixture fixture) : base(fixture)
    {
    }

    [Fact]
    public async Task use_combo_with_problem_details_as_endpoint()
    {
        // Should be good
        await Host.Scenario(x =>
        {
            x.Post.Json(new WolverineWebApi.NumberMessage(3)).ToUrl("/problems");
        });
        
        // should return problem details because the number > 5
        // Should be good
        var result = await Host.Scenario(x =>
        {
            x.Post.Json(new WolverineWebApi.NumberMessage(10)).ToUrl("/problems");
            x.ContentTypeShouldBe("application/problem+json");
            x.StatusCodeShouldBe(400);
        });

        var details = result.ReadAsJson<ProblemDetails>();
        
        details.Detail.ShouldBe("Number is bigger than 5");
    }

    [Fact]
    public async Task use_combo_as_handler_see_problem_details_catch()
    {
        NumberMessageHandler.Handled = false;

        // This should be invalid and stop
        await Host.InvokeAsync(new NumberMessage(10));
        NumberMessageHandler.Handled.ShouldBeFalse();

        // should be good because we're < 5
        await Host.InvokeAsync(new NumberMessage(3));
        NumberMessageHandler.Handled.ShouldBeTrue();
    }
}