using Alba;
using Microsoft.AspNetCore.Mvc;
using Shouldly;
using Wolverine.Tracking;
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
        // cleaning up expected change
        NumberMessageHandler.CalledBeforeOnlyOnHttpEndpoints = false;
        NumberMessageHandler.CalledBeforeOnlyOnMessageHandlers = false;
        
        // Should be good
        await Host.Scenario(x =>
        {
            x.Post.Json(new WolverineWebApi.NumberMessage(3)).ToUrl("/problems2");
            x.StatusCodeShouldBe(204);
        });
        
        NumberMessageHandler.CalledBeforeOnlyOnHttpEndpoints.ShouldBeTrue();
        NumberMessageHandler.CalledBeforeOnlyOnMessageHandlers.ShouldBeFalse();
        
        // should return problem details because the number > 5
        // Should be good
        var result = await Host.Scenario(x =>
        {
            x.Post.Json(new WolverineWebApi.NumberMessage(10)).ToUrl("/problems2");
            x.ContentTypeShouldBe("application/problem+json");
            x.StatusCodeShouldBe(400);
        });

        var details = result.ReadAsJson<ProblemDetails>();
        
        details.Detail.ShouldBe("Number is bigger than 5");
        

    }

    [Fact]
    public async Task use_combo_as_handler_see_problem_details_catch()
    {
        // cleaning up expected change
        NumberMessageHandler.CalledBeforeOnlyOnHttpEndpoints = false;
        NumberMessageHandler.CalledBeforeOnlyOnMessageHandlers = false;
        NumberMessageHandler.Handled = false;

        // This should be invalid and stop
        await Host.InvokeAsync(new NumberMessage(10));
        NumberMessageHandler.Handled.ShouldBeFalse();

        // should be good because we're < 5
        await Host.InvokeAsync(new NumberMessage(3));
        NumberMessageHandler.Handled.ShouldBeTrue();
        
        NumberMessageHandler.CalledBeforeOnlyOnHttpEndpoints.ShouldBeFalse();
        NumberMessageHandler.CalledBeforeOnlyOnMessageHandlers.ShouldBeTrue();
    }

    [Fact]
    public async Task will_not_go_bonkers_with_nullable_http_context()
    {
        var response = await Host.Scenario(x =>
        {
            x.Post.Json(new DoHybrid("go, go gadget")).ToUrl("/hybrid");
        });
        
        response.ReadAsText().ShouldBe("go, go gadget");

        await Host.InvokeMessageAndWaitAsync(new DoHybrid("now as a handler"));
    }
}