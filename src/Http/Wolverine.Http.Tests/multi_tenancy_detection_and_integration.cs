using System.Security.Claims;
using Alba;
using Alba.Security;
using IntegrationTests;
using Marten;
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

    protected async Task configure(Action<WolverineHttpOptions> configure)
    {
        var builder = WebApplication.CreateBuilder(Array.Empty<string>());
        builder.Services.AddScoped<IUserService, UserService>();

        builder.Services.AddMarten(Servers.PostgresConnectionString)
            .IntegrateWithWolverine();
        
        builder.Host.UseWolverine();
        
        // Setting up Alba stubbed authentication
        var securityStub = new AuthenticationStub()
            .With("foo", "bar")
            .With(JwtRegisteredClaimNames.Email, "guy@company.com")
            .WithName("jeremy");
        
        theHost = await AlbaHost.For(builder, app =>
        {
            app.MapWolverineEndpoints(configure);
        }, securityStub);
    }

    public async ValueTask DisposeAsync()
    {
        await theHost.StopAsync();
    }

    [Fact]
    public async Task get_the_tenant_id_from_route_value()
    {
        await configure(opts => opts.TenantId.IsRouteArgumentNamed("tenant"));
        
        var result = await theHost.Scenario(x => x.Get.Url("/tenant/route/foo"));
        
        result.ReadAsText().ShouldBe("foo");
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
            x.StatusCodeShouldBe(400);
        });

        var details = results.ReadAsJson<ProblemDetails>();
        details.Detail.ShouldBe(TenantIdDetection.NoMandatoryTenantIdCouldBeDetectedForThisHttpRequest);
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
}