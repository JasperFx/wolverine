using Alba;
using Shouldly;
using WolverineWebApi;

namespace Wolverine.Http.Tests;

public class simple_validation_in_http_endpoints : IntegrationContext
{
    public simple_validation_in_http_endpoints(AppFixture fixture) : base(fixture)
    {
    }

    [Fact]
    public async Task happy_path_with_ienumerable_string_validate()
    {
        await Scenario(x =>
        {
            x.Post.Json(new SimpleValidateHttpEnumerableMessage(3)).ToUrl("/simple-validation/ienumerable");
        });
    }

    [Fact]
    public async Task sad_path_with_ienumerable_string_validate()
    {
        await Scenario(x =>
        {
            x.Post.Json(new SimpleValidateHttpEnumerableMessage(20)).ToUrl("/simple-validation/ienumerable");
            x.StatusCodeShouldBe(400);
            x.ContentTypeShouldBe("application/problem+json");
        });
    }

    [Fact]
    public async Task happy_path_with_string_array_validate()
    {
        await Scenario(x =>
        {
            x.Post.Json(new SimpleValidateHttpStringArrayMessage(3)).ToUrl("/simple-validation/string-array");
        });
    }

    [Fact]
    public async Task sad_path_with_string_array_validate()
    {
        await Scenario(x =>
        {
            x.Post.Json(new SimpleValidateHttpStringArrayMessage(20)).ToUrl("/simple-validation/string-array");
            x.StatusCodeShouldBe(400);
            x.ContentTypeShouldBe("application/problem+json");
        });
    }

    [Fact]
    public async Task happy_path_with_async_string_array_validate()
    {
        await Scenario(x =>
        {
            x.Post.Json(new SimpleValidateHttpAsyncMessage(3)).ToUrl("/simple-validation/async");
        });
    }

    [Fact]
    public async Task sad_path_with_async_string_array_validate()
    {
        await Scenario(x =>
        {
            x.Post.Json(new SimpleValidateHttpAsyncMessage(20)).ToUrl("/simple-validation/async");
            x.StatusCodeShouldBe(400);
            x.ContentTypeShouldBe("application/problem+json");
        });
    }

    [Fact]
    public async Task happy_path_with_valuetask_string_array_validate()
    {
        await Scenario(x =>
        {
            x.Post.Json(new SimpleValidateHttpValueTaskMessage(3)).ToUrl("/simple-validation/valuetask");
        });
    }

    [Fact]
    public async Task sad_path_with_valuetask_string_array_validate()
    {
        await Scenario(x =>
        {
            x.Post.Json(new SimpleValidateHttpValueTaskMessage(20)).ToUrl("/simple-validation/valuetask");
            x.StatusCodeShouldBe(400);
            x.ContentTypeShouldBe("application/problem+json");
        });
    }
}
