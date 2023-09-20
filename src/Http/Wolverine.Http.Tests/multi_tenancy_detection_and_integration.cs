using System.Security.Claims;
using Alba;
using Alba.Security;
using IntegrationTests;
using Marten;
using Marten.Metadata;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.IdentityModel.JsonWebTokens;
using Shouldly;
using Wolverine.Http.Runtime;
using Wolverine.Http.Tests.Bugs;
using Wolverine.Marten;

namespace Wolverine.Http.Tests;

public class multi_tenancy_detection_and_integration : IAsyncDisposable, IDisposable
{
    private IAlbaHost theHost;

    public void Dispose()
    {
        theHost.Dispose();
    }

    // The configuration of the Wolverine.HTTP endpoints is the only variable
    // part of the test, so isolate all this test setup noise here so
    // each test can more clearly communicate the relationship between
    // Wolverine configuration and the desired behavior
    protected async Task configure(Action<WolverineHttpOptions> configure)
    {
        var builder = WebApplication.CreateBuilder(Array.Empty<string>());
        builder.Services.AddScoped<IUserService, UserService>();

        // Haven't gotten around to it yet, but there'll be some end to
        // end tests in a bit from the ASP.Net request all the way down
        // to the underlying tenant databases
        builder.Services.AddMarten(Servers.PostgresConnectionString)
            .IntegrateWithWolverine();
        
        // Defaults are good enough here
        builder.Host.UseWolverine(opts =>
        {
            opts.Policies.AutoApplyTransactions();
        });
        
        // Setting up Alba stubbed authentication so that we can fake
        // out ClaimsPrincipal data on requests later
        var securityStub = new AuthenticationStub()
            .With("foo", "bar")
            .With(JwtRegisteredClaimNames.Email, "guy@company.com")
            .WithName("jeremy");
        
        // Spinning up a test application using Alba 
        theHost = await AlbaHost.For(builder, app =>
        {
            app.MapWolverineEndpoints(configure);
        }, securityStub);

        await theHost.Services.GetRequiredService<IDocumentStore>().Advanced.Clean
            .DeleteDocumentsByTypeAsync(typeof(TenantTodo));
    }

    public async ValueTask DisposeAsync()
    {
        // Hey, this is important!
        // Make sure you clean up after your tests
        // to make the subsequent tests run cleanly
        await theHost.StopAsync();
    }

    [Fact]
    public async Task get_the_tenant_id_from_route_value()
    {
        // Set up a new application with the desired configuration
        await configure(opts => opts.TenantId.IsRouteArgumentNamed("tenant"));
        
        // Run a web request end to end in memory
        var result = await theHost.Scenario(x => x.Get.Url("/tenant/route/chartreuse"));
        
        // Make sure it worked!
        // ZZ Top FTW! https://www.youtube.com/watch?v=uTjgZEapJb8
        result.ReadAsText().ShouldBe("chartreuse");
    }

    [Fact]
    public async Task get_the_tenant_id_from_the_query_string()
    {
        await configure(opts => opts.TenantId.IsQueryStringValue("t"));
        
        var result = await theHost.Scenario(x => x.Get.Url("/tenant?t=bar"));
        
        result.ReadAsText().ShouldBe("bar");
    }
    
    [Fact]
    public async Task get_the_tenant_id_from_request_header()
    {
        await configure(opts => opts.TenantId.IsRequestHeaderValue("tenant"));
        
        var result = await theHost.Scenario(x =>
        {
            x.Get.Url("/tenant");
            
            // Alba is helping set up the request header
            // for me here
            x.WithRequestHeader("tenant", "green");
        });
        
        result.ReadAsText().ShouldBe("green");
    }

    [Fact]
    public async Task get_the_tenant_id_from_a_claim()
    {
        await configure(opts => opts.TenantId.IsClaimTypeNamed("tenant"));
        
        var result = await theHost.Scenario(x =>
        {
            x.Get.Url("/tenant");
            
            // Add a Claim to *only* this request
            x.WithClaim(new Claim("tenant", "blue"));
        });
        
        result.ReadAsText().ShouldBe("blue");
    }

    [Fact]
    public async Task detection_is_fall_through()
    {
        await configure(opts =>
        {
            opts.TenantId.IsClaimTypeNamed("tenant");
            opts.TenantId.IsQueryStringValue("tenant");
            opts.TenantId.IsRequestHeaderValue("tenant");
        });
        
        (await theHost.Scenario(x =>
        {
            x.Get.Url("/tenant?tenant=green");
            x.WithClaim(new Claim("tenant", "blue"));
            x.WithRequestHeader("tenant", "purple");
            
        })).ReadAsText().ShouldBe("blue");
        
        (await theHost.Scenario(x =>
        {
            x.Get.Url("/tenant?tenant=green");
            
        })).ReadAsText().ShouldBe("green");
        
        (await theHost.Scenario(x =>
        {
            x.Get.Url("/tenant?tenant=green");
            x.WithRequestHeader("tenant", "purple");
            
        })).ReadAsText().ShouldBe("green");
        
        (await theHost.Scenario(x =>
        {
            x.Get.Url("/tenant?tenant");
            x.WithRequestHeader("tenant", "purple");
            
        })).ReadAsText().ShouldBe("purple");
    }

    [Fact]
    public async Task require_tenant_id_happy_path()
    {
        await configure(opts =>
        {
            opts.TenantId.IsQueryStringValue("tenant");
            opts.TenantId.AssertExists();
        });

        // Got a 200? All good!
        await theHost.Scenario(x =>
        {
            x.Get.Url("/tenant?tenant=green");
        });
    }
    
    [Fact]
    public async Task require_tenant_id_sad_path()
    {
        await configure(opts =>
        {
            opts.TenantId.IsQueryStringValue("tenant");
            opts.TenantId.AssertExists();
        });

        var results = await theHost.Scenario(x =>
        {
            x.Get.Url("/tenant");
            
            // Tell Alba we expect a non-200 response
            x.StatusCodeShouldBe(400);
        });

        // Alba's helpers to deserialize JSON responses
        // to a strong typed object for easy
        // assertions
        var details = results.ReadAsJson<ProblemDetails>();
        
        // I like to refer to constants in test assertions sometimes
        // so that you can tweak error messages later w/o breaking
        // automated tests. And inevitably regret it when I 
        // don't do this
        details.Detail.ShouldBe(TenantIdDetection
            .NoMandatoryTenantIdCouldBeDetectedForThisHttpRequest);
    }

    [Fact]
    public async Task end_to_end_through_marten()
    {
        await configure(opts =>
        {
            opts.TenantId.IsRequestHeaderValue("tenant");
            opts.TenantId.IsQueryStringValue("tenant");
        });
        
        // Create todo to "red"
        await theHost.Scenario(x =>
        {
            x.Post.Json(new CreateTodo("one", "red one")).ToUrl("/todo/create");
            x.WithRequestHeader("tenant", "red");
            x.StatusCodeShouldBe(204);
        });
        
        // Create same id todo to "blue"
        await theHost.Scenario(x =>
        {
            x.Post.Json(new CreateTodo("one", "blue one")).ToUrl("/todo/create");
            x.WithRequestHeader("tenant", "blue");
            x.StatusCodeShouldBe(204);
        });
        
        // retrieve red one
        var result1 = await theHost.Scenario(x =>
        {
            x.Get.Url("/todo/one");
            x.WithRequestHeader("tenant", "red");
        });
        result1.ReadAsJson<TenantTodo>().Description.ShouldBe("red one");
        
        // retrieve blue one
        var result2 = await theHost.Scenario(x =>
        {
            x.Get.Url("/todo/one");
            x.WithRequestHeader("tenant", "blue");
        });
        result2.ReadAsJson<TenantTodo>().Description.ShouldBe("blue one");
    }
}


public static class TenantedEndpoints
{
    [WolverineGet("/tenant/route/{tenant}")]
    public static string GetTenantIdFromRoute(IMessageBus bus)
    {
        return bus.TenantId;
    }

    [WolverineGet("/tenant")]
    public static string GetTenantIdFromWhatever(IMessageBus bus)
    {
        return bus.TenantId;
    }
    
    [WolverineGet("/todo/{id}")]
    public static Task<TenantTodo?> Get(string id, IQuerySession session)
    {
        return session.LoadAsync<TenantTodo>(id);
    }

    [WolverinePost("/todo/create")]
    public static IMartenOp Create(CreateTodo command)
    {
        return MartenOps.Insert(new TenantTodo
        {
            Id = command.Id,
            Description = command.Description
        });
    }


}

public record CreateTodo(string Id, string Description);

public class TenantTodo : ITenanted
{
    public string Id { get; set; }
    public string Description { get; set; }
    public string TenantId { get; set; }
}