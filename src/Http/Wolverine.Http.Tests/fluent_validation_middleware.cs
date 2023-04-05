using System.Diagnostics;
using Alba;
using Microsoft.AspNetCore.Mvc;
using Shouldly;
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
}