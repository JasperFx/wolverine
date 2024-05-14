using Alba;
using Microsoft.AspNetCore.Mvc;
using Wolverine.Http.Tests.DifferentAssembly.Validation;
using WolverineWebApi.Validation;

namespace Wolverine.Http.Tests;

public class fluent_validation_middleware : IntegrationContext
{
    public fluent_validation_middleware(AppFixture fixture) : base(fixture)
    {
    }

    [Fact]
    public async Task one_validator_happy_path()
    {
        var createCustomer = new CreateCustomer("Creed", "Humphrey", "11111");

        // Succeeds w/ a 200
        var result = await Scenario(x =>
        {
            x.Post.Json(createCustomer).ToUrl("/validate/customer");
            x.ContentTypeShouldBe("text/plain");
        });
    }

    [Fact]
    public async Task one_validator_sad_path()
    {
        var createCustomer = new CreateCustomer(null, "Humphrey", "11111");

        var results = await Scenario(x =>
        {
            x.Post.Json(createCustomer).ToUrl("/validate/customer");
            x.ContentTypeShouldBe("application/problem+json");
            x.StatusCodeShouldBe(400);
        });

        // Just proving that we have ProblemDetails content
        // in the request
        var problems = results.ReadAsJson<ProblemDetails>();
    }
    [Fact]
    public async Task one_validator_sad_path_in_different_assembly()
    {
        var createCustomer = new CreateCustomer2(null, "Humphrey", "11111");

        var results = await Scenario(x =>
        {
            x.Post.Json(createCustomer).ToUrl("/validate2/customer");
            x.ContentTypeShouldBe("application/problem+json");
            x.StatusCodeShouldBe(400);
        });

        // Just proving that we have ProblemDetails content
        // in the request
        var problems = results.ReadAsJson<ProblemDetails>();
    }

    [Fact]
    public async Task two_validator_happy_path()
    {
        var createUser = new CreateUser("Trey", "Smith", "11111", "12345678");

        // Succeeds w/ a 200
        await Scenario(x =>
        {
            x.Post.Json(createUser).ToUrl("/validate/user");
            x.ContentTypeShouldBe("text/plain");
        });
    }

    [Fact]
    public async Task two_validator_sad_path()
    {
        var createUser = new CreateUser("Trey", "Smith", "11111", "123456");

        var results = await Scenario(x =>
        {
            x.Post.Json(createUser).ToUrl("/validate/user");
            x.ContentTypeShouldBe("application/problem+json");
            x.StatusCodeShouldBe(400);
        });

        var problems = results.ReadAsJson<ProblemDetails>();
    }

    [Fact]
    public async Task when_using_compound_handler_validation_is_called_before_load()
    {
        var blockUser = new BlockUser(null);

        var results = await Scenario(x =>
        {
            x.Delete.Json(blockUser).ToUrl("/validate/user-compound");
            x.ContentTypeShouldBe("application/problem+json");
            x.StatusCodeShouldBe(400);
        });

        var problems = results.ReadAsJson<ProblemDetails>();
    }
}