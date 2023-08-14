using Alba;
using IntegrationTests;
using Marten;
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.DependencyInjection;
using Newtonsoft.Json;
using Newtonsoft.Json.Serialization;
using Shouldly;
using Wolverine.Http.Tests.Bugs;
using Wolverine.Marten;

namespace Wolverine.Http.Tests;

public class using_newtonsoft_for_serialization
{
    [Fact]
    public async Task end_to_end()
    {
        var builder = WebApplication.CreateBuilder(Array.Empty<string>());
        builder.Services.AddScoped<IUserService, UserService>();

        builder.Services.AddMarten(Servers.PostgresConnectionString)
            .IntegrateWithWolverine();
        
        builder.Host.UseWolverine();

        await using var host = await AlbaHost.For(builder, app =>
        {
            app.MapWolverineEndpoints(opts =>
            {
                // This is just to prove Newtonsoft is in effect
                opts.UseNewtonsoftJson(settings => settings.TypeNameHandling = TypeNameHandling.All);
            });
        });

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