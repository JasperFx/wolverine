using Alba;
using IntegrationTests;
using Marten;
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.DependencyInjection;
using Newtonsoft.Json;
using Shouldly;
using Wolverine.Http.Tests.Bugs;
using Wolverine.Marten;

namespace Wolverine.Http.Tests;

public class using_newtonsoft_for_serialization
{
    [Fact]
    public async Task end_to_end()
    {
        #region sample_use_newtonsoft_for_http_serialization

        var builder = WebApplication.CreateBuilder([]);
        builder.Services.AddScoped<IUserService, UserService>();

        builder.Services.AddMarten(Servers.PostgresConnectionString)
            .IntegrateWithWolverine();

        builder.Host.UseWolverine(opts =>
        {
            opts.Discovery.IncludeAssembly(GetType().Assembly);
        });

        await using var host = await AlbaHost.For(builder, app =>
        {
            app.MapWolverineEndpoints(opts =>
            {
                // Opt into using Newtonsoft.Json for JSON serialization just with Wolverine.HTTP routes
                // Configuring the JSON serialization is optional
                opts.UseNewtonsoftJsonForSerialization(settings => settings.TypeNameHandling = TypeNameHandling.All);
            });
        });

        #endregion

        var result = await host.Scenario(x =>
        {
            x.Post.Json(new NumberRequest(3, 4)).ToUrl("/newtonsoft/numbers");
        });

        var text = result.ReadAsText();

        text.ShouldBe("{\"$type\":\"Wolverine.Http.Tests.MathResponse, Wolverine.Http.Tests\",\"Sum\":7,\"Product\":12}");

    }
}

public record NumberRequest(int X, int Y);
public record MathResponse(int Sum, int Product);

public static class MathEndpoint
{
    [WolverinePost("/newtonsoft/numbers")]
    public static MathResponse Post(NumberRequest request)
    {
        return new MathResponse(request.X + request.Y, request.X * request.Y);
    }
}