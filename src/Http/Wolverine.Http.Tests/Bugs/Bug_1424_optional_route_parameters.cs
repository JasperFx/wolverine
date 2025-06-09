using Alba;
using IntegrationTests;
using Marten;
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.DependencyInjection;
using Wolverine.Marten;

namespace Wolverine.Http.Tests.Bugs;

public class Bug_1424_optional_route_parameters
{
    [Fact]
    public async Task allows_optional_route_parameters_on_nullable_parameters()
    {
        var builder = WebApplication.CreateBuilder([]);
        
        builder.Services.AddMarten(opts =>
        {
            // Establish the connection string to your Marten database
            opts.Connection(Servers.PostgresConnectionString);
            opts.DisableNpgsqlLogging = true;
        }).IntegrateWithWolverine();

        builder.Host.UseWolverine(opts =>
        {
            opts.Discovery.DisableConventionalDiscovery().IncludeType<Bug1424Controller>();
            opts.ApplicationAssembly = GetType().Assembly;
        });

        builder.Services.AddWolverineHttp();

        builder.Services.AddWolverineHttp();
        builder.Services.AddControllers();

        // This is using Alba, which uses WebApplicationFactory under the covers
        await using var host = await AlbaHost.For(builder, app =>
        {
            app.MapWolverineEndpoints();
        });

        await host.Scenario(x =>
        {
            x.Get.Url("/bugs/1424/nullable/required/00000000-0000-0000-0000-000000000000/5");
            x.StatusCodeShouldBe(200);
            x.ContentShouldBe("required:00000000-0000-0000-0000-000000000000:5");
        });

        await host.Scenario(x =>
        {
            x.Get.Url("/bugs/1424/nullable/required");
            x.StatusCodeShouldBe(200);
            x.ContentShouldBe("required:(null):(null)");
        });

        await host.Scenario(x =>
        {
            x.Get.Url("/bugs/1424/nullable");
            x.StatusCodeShouldBe(200);
            x.ContentShouldBe("(null):(null):(null)");
        });
        
        await host.Scenario(x =>
        {
            x.Get.Url("/bugs/1424/nullable/required/not-a-guid");
            x.StatusCodeShouldBe(404);
        });
    }
    
    [Fact]
    public async Task allows_optional_route_parameters_on_non_nullable_parameters()
    {
        var builder = WebApplication.CreateBuilder([]);
        
        builder.Services.AddMarten(opts =>
        {
            // Establish the connection string to your Marten database
            opts.Connection(Servers.PostgresConnectionString);
            opts.DisableNpgsqlLogging = true;
        }).IntegrateWithWolverine();

        builder.Host.UseWolverine(opts =>
        {
            opts.Discovery.DisableConventionalDiscovery().IncludeType<Bug1424Controller>();
            opts.ApplicationAssembly = GetType().Assembly;
        });

        builder.Services.AddWolverineHttp();

        builder.Services.AddWolverineHttp();
        builder.Services.AddControllers();

        // This is using Alba, which uses WebApplicationFactory under the covers
        await using var host = await AlbaHost.For(builder, app =>
        {
            app.MapWolverineEndpoints();
        });

        await host.Scenario(x =>
        {
            x.Get.Url("/bugs/1424/nonnullable/required/00000000-0000-0000-0000-000000000000/5");
            x.StatusCodeShouldBe(200);
            x.ContentShouldBe("required:00000000-0000-0000-0000-000000000000:5");
        });

        await host.Scenario(x =>
        {
            x.Get.Url("/bugs/1424/nonnullable/required");
            x.StatusCodeShouldBe(200);
            x.ContentShouldBe("required:00000000-0000-0000-0000-000000000000:0");
        });

        await host.Scenario(x =>
        {
            x.Get.Url("/bugs/1424/nonnullable");
            x.StatusCodeShouldBe(200);
            x.ContentShouldBe("(null):00000000-0000-0000-0000-000000000000:0");
        });
        
        await host.Scenario(x =>
        {
            x.Get.Url("/bugs/1424/nonnullable/required/not-a-guid");
            x.StatusCodeShouldBe(404);
        });
    }
    
}

public class Bug1424Controller
{
    [WolverineGet("/bugs/1424/nullable/{first?}/{second?}/{third?}", Name = "Bug1424_nullable")]
    public string Get(string? first, Guid? second, int? third)
    {
        return string.Format("{0}:{1}:{2}", first ?? "(null)", second?.ToString() ?? "(null)", third?.ToString() ?? "(null)");
    }
    
    [WolverineGet("/bugs/1424/nonnullable/{first?}/{second?}/{third?}", Name = "Bug1424_nonnullable")]
    public string Get(string first, Guid second, int third)
    {
        return string.Format("{0}:{1}:{2}", first ?? "(null)", second, third);
    }
}