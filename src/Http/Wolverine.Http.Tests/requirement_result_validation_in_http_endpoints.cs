using Alba;
using Shouldly;
using WolverineWebApi;

namespace Wolverine.Http.Tests;

public class requirement_result_validation_in_http_endpoints : IntegrationContext
{
    public requirement_result_validation_in_http_endpoints(AppFixture fixture) : base(fixture)
    {
    }

    [Fact]
    public async Task happy_path_with_requirement_result_validate()
    {
        await Scenario(x =>
        {
            x.Post.Json(new RequirementResultHttpMessage(3)).ToUrl("/requirement-result/sync");
        });
    }

    [Fact]
    public async Task sad_path_with_requirement_result_validate()
    {
        await Scenario(x =>
        {
            x.Post.Json(new RequirementResultHttpMessage(20)).ToUrl("/requirement-result/sync");
            x.StatusCodeShouldBe(400);
            x.ContentTypeShouldBe("application/problem+json");
        });
    }

    [Fact]
    public async Task happy_path_with_async_requirement_result_validate()
    {
        await Scenario(x =>
        {
            x.Post.Json(new RequirementResultHttpAsyncMessage(3)).ToUrl("/requirement-result/async");
        });
    }

    [Fact]
    public async Task sad_path_with_async_requirement_result_validate()
    {
        await Scenario(x =>
        {
            x.Post.Json(new RequirementResultHttpAsyncMessage(20)).ToUrl("/requirement-result/async");
            x.StatusCodeShouldBe(400);
            x.ContentTypeShouldBe("application/problem+json");
        });
    }

    [Fact]
    public async Task happy_path_with_empty_messages_requirement_result_validate()
    {
        await Scenario(x =>
        {
            x.Post.Json(new RequirementResultHttpEmptyMessagesMessage(3)).ToUrl("/requirement-result/empty-messages");
        });
    }

    [Fact]
    public async Task sad_path_with_empty_messages_returns_invalid_request()
    {
        var result = await Scenario(x =>
        {
            x.Post.Json(new RequirementResultHttpEmptyMessagesMessage(20)).ToUrl("/requirement-result/empty-messages");
            x.StatusCodeShouldBe(400);
            x.ContentTypeShouldBe("application/problem+json");
        });

        var json = result.ReadAsJson<Microsoft.AspNetCore.Mvc.ProblemDetails>();
        json!.Detail.ShouldBe("Invalid Request");
    }
}
