using Alba;
using IntegrationTests;
using Marten;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Wolverine.Marten;

namespace Wolverine.Http.Tests.Bugs;

public class Bug_1961_reserved_characters_in_form_field
{
    [Fact]
    public async Task generate_code_correctly()
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
            opts.Discovery.DisableConventionalDiscovery().IncludeType(typeof(TrainingRequestHandler));
            opts.ApplicationAssembly = GetType().Assembly;
        });

        builder.Services.AddWolverineHttp();

        // This is using Alba, which uses WebApplicationFactory under the covers
        await using var host = await AlbaHost.For(builder, app =>
        {
            app.MapWolverineEndpoints(x =>
            {
                
            });
        });

        var result = await host.Scenario(x =>
        {
            x.Post.FormData(new (){ { "rechtliche_hinweise/akzeptiert", "Albert" } })
                .ToUrl("/angebot/training");

            x.StatusCodeShouldBe(302);
        });
        
        var result2 = await host.Scenario(x =>
        {
            x.Post.FormData(new (){ { "foo-bar", "Albert" } })
                .ToUrl("/angebot/training2");

            x.StatusCodeShouldBe(302);
        });
    }
}

// For https://github.com/JasperFx/wolverine/issues/1961
public record TrainingRequest(
    [FromForm(Name = "rechtliche_hinweise/akzeptiert")]
    bool? rechtlicheHinweiseAkzeptiert
);

public record TrainingRequest2(
    [FromForm(Name = "foo-bar")]
    bool? FooBar
);

public static class TrainingRequestHandler
{
    [WolverinePost("angebot/training")]
    public static IResult Post([AsParameters()]TrainingRequest egal)

    {
        return Results.Redirect($"/vielen-dank");
    }
    
    [WolverinePost("angebot/training2")]
    public static IResult Post([AsParameters()]TrainingRequest2 egal)

    {
        return Results.Redirect($"/vielen-dank");
    }
    
}