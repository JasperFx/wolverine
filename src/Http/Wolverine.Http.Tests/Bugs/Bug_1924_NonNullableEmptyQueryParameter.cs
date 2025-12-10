using Alba;
using IntegrationTests;
using Marten;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.DependencyInjection;
using Wolverine.Marten;

namespace Wolverine.Http.Tests.Bugs;

public class Bug_1924_NonNullableEmptyQueryParameter
{
    [Fact]
    public async Task non_nullable_empty_string_query_parameters_dont_throw()
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
            opts.Discovery.DisableConventionalDiscovery().IncludeType<Bug1924Controller>();
            opts.ApplicationAssembly = GetType().Assembly;
        });

        builder.Services.AddWolverineHttp();

        // This is using Alba, which uses WebApplicationFactory under the covers
        await using var host = await AlbaHost.For(builder, app => { app.MapWolverineEndpoints(); });

        await host.Scenario(x =>
        {
            x.Get.Url("/bugs/1924/nullable?first=Test");
            x.StatusCodeShouldBe(200);
            x.ContentShouldBe("Test");
        });
        
        await host.Scenario(x =>
        {
            x.Get.Url("/bugs/1924/nullable");
            x.StatusCodeShouldBe(200);
            x.ContentShouldBe("");
        });
    }
}

public class Bug1924Controller
{
    [WolverineGet("/bugs/1924/nullable", Name = "Bug1924_nullable")]
    public string Get([FromQuery] string first)
    {
        if (first == null) throw new ArgumentNullException(nameof(first));
        return first.ToString();
    }
}
