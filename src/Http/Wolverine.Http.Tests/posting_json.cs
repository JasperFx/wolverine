using Microsoft.AspNetCore.Mvc;
using Shouldly;
using WolverineWebApi;

namespace Wolverine.Http.Tests;

public class posting_json : IntegrationContext
{
    public posting_json(AppFixture fixture) : base(fixture)
    {
    }

    #region sample_post_json_happy_path
    [Fact]
    public async Task post_json_happy_path()
    {
        // This test is using Alba to run an end to end HTTP request
        // and interrogate the results
        var response = await Scenario(x =>
        {
            x.Post.Json(new Question { One = 3, Two = 4 }).ToUrl("/question");
            x.WithRequestHeader("accept", "application/json");
        });

        var result = await response.ReadAsJsonAsync<ArithmeticResults>();

        result.Product.ShouldBe(12);
        result.Sum.ShouldBe(7);
    }

    #endregion

    [Fact]
    public async Task post_json_happy_path_with_star_slash_star()
    {
        // This test is using Alba to run an end to end HTTP request
        // and interrogate the results
        var response = await Scenario(x =>
        {
            x.Post.Json(new Question { One = 3, Two = 4 }).ToUrl("/question");
            x.WithRequestHeader("accept", "*/*");
        });

        var result = await response.ReadAsJsonAsync<ArithmeticResults>();

        result.Product.ShouldBe(12);
        result.Sum.ShouldBe(7);
    }
    
    [Fact]
    public async Task post_json_happy_path_with_no_accept()
    {
        // This test is using Alba to run an end to end HTTP request
        // and interrogate the results
        var response = await Scenario(x =>
        {
            x.Post.Json(new Question { One = 3, Two = 4 }).ToUrl("/question");
        });

        var result = await response.ReadAsJsonAsync<ArithmeticResults>();

        result.Product.ShouldBe(12);
        result.Sum.ShouldBe(7);
    }

    [Fact]
    public async Task post_json_happy_path_with_accepts_problem_details()
    {
        // This test is using Alba to run an end to end HTTP request
        // and interrogate the results
        var response = await Scenario(x =>
        {
            x.Post.Json(new Question { One = 3, Two = 4 }).ToUrl("/question");
            x.WithRequestHeader("accept", "application/problem+json");
        });

        var result = await response.ReadAsJsonAsync<ArithmeticResults>();

        result.Product.ShouldBe(12);
        result.Sum.ShouldBe(7);
    }

    [Fact]
    public async Task post_json_garbage_get_400()
    {
        var response = await Scenario(x =>
        {
            x.Post.Text("garbage").ToUrl("/question");
            x.WithRequestHeader("content-type", "application/json");
            x.StatusCodeShouldBe(400);
        });
    }

    [Fact]
    public async Task post_text_get_415()
    {
        var response = await Scenario(x =>
        {
            x.Post.Text("garbage").ToUrl("/question");
            x.WithRequestHeader("content-type", "text/plain");
            x.StatusCodeShouldBe(415);
        });
    }

    [Fact]
    public async Task post_json_but_accept_text_get_406()
    {
        var response = await Scenario(x =>
        {
            x.Post.Json(new Question { One = 3, Two = 4 }).ToUrl("/question");
            x.WithRequestHeader("accept", "text/plain");
            x.StatusCodeShouldBe(406);
        });
    }

    // GH-4528: this used to assert 204. A client that disconnects mid-upload then showed up as a
    // *successful* no-content response in access logs and metrics, which is exactly how a rash of aborted
    // uploads stays invisible. Nothing is written to an aborted request's socket anyway, so the status here
    // exists only for logging -- and it must not read as success. 499 is the widely-understood
    // "client closed request" convention.
    [Fact]
    public async Task reading_json_from_canceled_request_does_not_report_success()
    {
        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();

        var response = await Scenario(x =>
        {
            x.ConfigureHttpContext(ctx =>
            {
                ctx.Request.HttpContext.RequestAborted = cts.Token;
            });
            x.Post.Json(new Question { One = 3, Two = 4 }).ToUrl("/question");
            x.WithRequestHeader("accept", "application/json");
            x.StatusCodeShouldBe(HttpHandler.ClientClosedRequest);
        });
    }

    // GH-4528: a body read that fails with anything other than JsonException used to return a naked 400 --
    // no ProblemDetails, no body, no Content-Type -- and 400 was the wrong class besides. What lands there is
    // a type System.Text.Json cannot handle, which is a server-side configuration bug no change to the
    // request can fix, so it is now a 500 that names the type and the exception.
    [Fact]
    public async Task a_serialization_failure_that_is_not_malformed_json_is_a_500_problem_details()
    {
        var response = await Scenario(x =>
        {
            x.Post.Json(new UndeserializableRequest()).ToUrl("/undeserializable");
            x.WithRequestHeader("accept", "application/json");
            x.StatusCodeShouldBe(500);
        });

        var problem = await response.ReadAsJsonAsync<ProblemDetails>();

        problem.ShouldNotBeNull();
        problem.Title.ShouldBe("Request body could not be deserialized");
        problem.Status.ShouldBe(500);
        problem.Detail.ShouldNotBeNull();
        problem.Detail.ShouldContain("server side serialization problem");
        problem.Detail.ShouldContain("NotSupportedException");
        problem.Extensions["targetType"]?.ToString()!.ShouldContain("UndeserializableRequest");
    }

    // ...while genuinely malformed JSON keeps its 400 ProblemDetails, unchanged.
    [Fact]
    public async Task malformed_json_is_still_a_400_problem_details()
    {
        var response = await Scenario(x =>
        {
            x.Post.Text("{ this is not json ").ToUrl("/question");
            x.WithRequestHeader("content-type", "application/json");
            x.WithRequestHeader("accept", "application/json");
            x.StatusCodeShouldBe(400);
        });

        var problem = await response.ReadAsJsonAsync<ProblemDetails>();

        problem.ShouldNotBeNull();
        problem.Title.ShouldBe("Invalid JSON format");
        problem.Status.ShouldBe(400);
    }
}
