using System.Text.Json;
using Alba;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.DependencyInjection;
using Shouldly;
using Wolverine.Http.Runtime;
using Xunit;

namespace Wolverine.Http.Tests.Marten;

/// <summary>
/// GH-5353. The 409 mapping takes a factory for the ProblemDetails it returns, so an API can keep exception
/// messages (stream ids, CLR type names) out of the response body.
/// </summary>
public class conflict_problem_details_factory_5353(AppFixture fixture) : IntegrationContext(fixture)
{
    [Fact]
    public void default_shape_is_unchanged_without_a_factory()
    {
        var problem = ConflictMapping.ToProblemDetails(new InvalidOperationException("boom"), new WolverineHttpOptions());

        problem.Status.ShouldBe(409);
        problem.Title.ShouldBe("Conflict");
        problem.Detail.ShouldBe("boom");
    }

    [Fact]
    public void a_factory_without_a_status_gets_409()
    {
        var options = new WolverineHttpOptions
        {
            ConflictProblemDetailsFactory = _ => new ProblemDetails { Detail = "retry" }
        };

        var problem = ConflictMapping.ToProblemDetails(new InvalidOperationException("boom"), options);

        problem.Status.ShouldBe(409);
        problem.Detail.ShouldBe("retry");
    }

    [Fact]
    public async Task the_factory_shapes_the_409_response()
    {
        var options = Host.Services.GetRequiredService<WolverineHttpOptions>();
        options.ConflictProblemDetailsFactory = _ => new ProblemDetails
        {
            Status = 409,
            Title = "Conflict",
            Detail = "The resource was modified by another request. Fetch the current version and retry."
        };

        try
        {
            var created = await Host.Scenario(x =>
            {
                x.Post.Url("/seatholds/create");
                x.StatusCodeShouldBeOk();
            });
            var id = JsonSerializer.Deserialize<Guid>(await created.ReadAsTextAsync());

            var result = await Host.Scenario(x =>
            {
                x.Post.Url($"/seatholds/{id}/optimistic-conflict");
                x.StatusCodeShouldBe(409);
            });

            var body = await result.ReadAsTextAsync();
            body.ShouldContain("Fetch the current version and retry");
            body.ShouldNotContain("Unexpected starting version number");
        }
        finally
        {
            options.ConflictProblemDetailsFactory = null;
        }
    }
}
