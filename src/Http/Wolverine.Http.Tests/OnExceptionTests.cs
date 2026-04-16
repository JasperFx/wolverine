using Alba;
using Shouldly;
using WolverineWebApi;
using Xunit;
using Xunit.Abstractions;

namespace Wolverine.Http.Tests;

public class OnExceptionTests : IntegrationContext
{
    private readonly ITestOutputHelper _output;

    public OnExceptionTests(AppFixture fixture, ITestOutputHelper output) : base(fixture)
    {
        _output = output;
    }

    [Fact]
    public async Task handler_level_on_exception_returns_problem_details()
    {
        var result = await Scenario(x =>
        {
            x.Get.Url("/on-exception/simple");
            x.StatusCodeShouldBe(500);
            x.ContentTypeShouldBe("application/problem+json");
        });

        var problem = result.ReadAsJson<Microsoft.AspNetCore.Mvc.ProblemDetails>();
        problem.ShouldNotBeNull();
        problem!.Title.ShouldBe("Custom Error");
        problem.Detail.ShouldBe("Something went wrong");
    }

    [Fact]
    public async Task specific_exception_matched_before_general()
    {
        var result = await Scenario(x =>
        {
            x.Get.Url("/on-exception/specific");
            x.StatusCodeShouldBe(422);
            x.ContentTypeShouldBe("application/problem+json");
        });

        var problem = result.ReadAsJson<Microsoft.AspNetCore.Mvc.ProblemDetails>();
        problem.ShouldNotBeNull();
        problem!.Title.ShouldBe("Specific Error");
    }

    [Fact]
    public async Task general_exception_caught_when_no_specific_match()
    {
        var result = await Scenario(x =>
        {
            x.Get.Url("/on-exception/general");
            x.StatusCodeShouldBe(500);
            x.ContentTypeShouldBe("application/problem+json");
        });

        var problem = result.ReadAsJson<Microsoft.AspNetCore.Mvc.ProblemDetails>();
        problem.ShouldNotBeNull();
        problem!.Title.ShouldBe("General Error");
    }

    [Fact]
    public async Task async_on_exception_handler()
    {
        var result = await Scenario(x =>
        {
            x.Get.Url("/on-exception/async");
            x.StatusCodeShouldBe(500);
            x.ContentTypeShouldBe("application/problem+json");
        });

        var problem = result.ReadAsJson<Microsoft.AspNetCore.Mvc.ProblemDetails>();
        problem.ShouldNotBeNull();
        problem!.Title.ShouldBe("Async Error");
    }

    [Fact]
    public async Task on_exception_with_finally_both_run()
    {
        ExceptionWithFinallyEndpoints.Actions.Clear();

        var result = await Scenario(x =>
        {
            x.Get.Url("/on-exception/with-finally");
            x.StatusCodeShouldBe(500);
        });

        // Handler threw, OnException handled it, Finally always runs
        ExceptionWithFinallyEndpoints.Actions.ShouldContain("Handler");
        ExceptionWithFinallyEndpoints.Actions.ShouldContain("OnException");
        ExceptionWithFinallyEndpoints.Actions.ShouldContain("Finally");
    }

    [Fact]
    public async Task unmatched_exception_propagates()
    {
        // InvalidOperationException is thrown but only CustomHttpException has a handler
        // Should propagate as a 500 from the default exception handling
        await Should.ThrowAsync<Exception>(async () =>
        {
            await Scenario(x =>
            {
                x.Get.Url("/on-exception/unmatched");
            });
        });
    }

    [Fact]
    public async Task no_error_does_not_invoke_on_exception()
    {
        var result = await Scenario(x =>
        {
            x.Get.Url("/on-exception/no-error");
            x.StatusCodeShouldBeOk();
        });

        result.ReadAsText().ShouldBe("All good");
    }

    [Fact]
    public async Task void_on_exception_still_swallows()
    {
        VoidExceptionEndpoints.LastException = "";

        // Void return means exception is swallowed but nothing is written
        // The response will have no content body (empty)
        await Scenario(x =>
        {
            x.Get.Url("/on-exception/void");
            // Exception swallowed, but no response body is written
            // Status code should be 200 since nothing writes an error
            x.StatusCodeShouldBeOk();
        });

        VoidExceptionEndpoints.LastException.ShouldBe("Void handler error");
    }

    [Fact]
    public async Task middleware_on_exception_handler()
    {
        var result = await Scenario(x =>
        {
            x.Get.Url("/on-exception/middleware");
            x.StatusCodeShouldBe(500);
            x.ContentTypeShouldBe("application/problem+json");
        });

        var problem = result.ReadAsJson<Microsoft.AspNetCore.Mvc.ProblemDetails>();
        problem.ShouldNotBeNull();
        problem!.Title.ShouldBe("Global Error Handler");
    }
}
