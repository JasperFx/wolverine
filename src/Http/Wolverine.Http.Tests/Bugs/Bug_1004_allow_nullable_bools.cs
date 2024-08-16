using Alba;
using IntegrationTests;
using Marten;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.AspNetCore.Mvc;
using Microsoft.CodeAnalysis;
using Microsoft.Extensions.DependencyInjection;
using Shouldly;
using Wolverine.Marten;

namespace Wolverine.Http.Tests.Bugs;

public class Bug_1004_allow_nullable_bools
{
    [Fact]
    public async Task allows_nullable_bools()
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
            opts.Discovery.DisableConventionalDiscovery();
            opts.ApplicationAssembly = GetType().Assembly;
        });
        
        builder.Services.AddControllers();

        // This is using Alba, which uses WebApplicationFactory under the covers
        await using var host = await AlbaHost.For(builder, app =>
        {
            app.MapWolverineEndpoints();
        });

        await host.Scenario(x =>
        {
            x.Get.Url("/bugs/1004");
            x.StatusCodeShouldBe(200);
        });

        await host.Scenario(x =>
        {
            x.Get.Url("/bugs/1004?test=true");
            x.StatusCodeShouldBe(200);
            x.ContentShouldBe("True");
        });

        await host.Scenario(x =>
        {
            x.Get.Url("/bugs/1004?test=false");
            x.StatusCodeShouldBe(200);
            x.ContentShouldBe("False");
        });
    }
}

public class Bug1004Controller
{
    [WolverineGet("/bugs/1004", Name = "Bug1004")]
    public string Get(bool? test = null)
    {
        return test?.ToString() ?? "<null>";
    }
}
