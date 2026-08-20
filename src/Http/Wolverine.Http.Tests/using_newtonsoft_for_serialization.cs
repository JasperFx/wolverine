using Alba;
using IntegrationTests;
using Marten;
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.DependencyInjection;
using Newtonsoft.Json;
using Shouldly;
using Wolverine.Http.Newtonsoft;
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

        builder.Services.AddWolverineHttp();
        // As of Wolverine 6.0, Newtonsoft.Json HTTP support lives in the
        // separate WolverineFx.Http.Newtonsoft package — register its
        // services here, then opt in via UseNewtonsoftJsonForSerialization()
        // below.
        builder.Services.AddWolverineHttpNewtonsoft();

        await using var host = await AlbaHost.For(builder, app =>
        {
            app.MapWolverineEndpoints(opts =>
            {
                // Opt into using Newtonsoft.Json for JSON serialization just with Wolverine.HTTP routes
                // Configuring the JSON serialization is optional. This extension method comes from
                // the WolverineFx.Http.Newtonsoft package (using Wolverine.Http.Newtonsoft;).
                opts.UseNewtonsoftJsonForSerialization(settings => settings.TypeNameHandling = TypeNameHandling.All);
            });
        });

        #endregion

        var result = await host.Scenario(x =>
        {
            x.Post.Json(new NumberRequest(3, 4)).ToUrl("/newtonsoft/numbers");
        });

        var text = await result.ReadAsTextAsync();

        text.ShouldBe("{\"$type\":\"Wolverine.Http.Tests.MathResponse, Wolverine.Http.Tests\",\"Sum\":7,\"Product\":12}");

    }

    // The Newtonsoft body reader is a MethodCall whose ReturnVariable IS the chain's
    // RequestBodyVariable, so the middleware message-replacement pass in
    // HttpChain.DetermineFrames must not flip the reader's own variable declaration
    // into an assignment. This covers both that guard and message replacement working
    // with the Newtonsoft serializer at all
    [Fact]
    public async Task before_middleware_can_replace_the_request_body()
    {
        var builder = WebApplication.CreateBuilder([]);
        builder.Services.AddScoped<IUserService, UserService>();

        builder.Services.AddMarten(Servers.PostgresConnectionString)
            .IntegrateWithWolverine();

        builder.Host.UseWolverine(opts =>
        {
            opts.Discovery.IncludeAssembly(GetType().Assembly);
        });

        builder.Services.AddWolverineHttp();
        builder.Services.AddWolverineHttpNewtonsoft();

        await using var host = await AlbaHost.For(builder, app =>
        {
            app.MapWolverineEndpoints(opts => opts.UseNewtonsoftJsonForSerialization());
        });

        var result = await host.Scenario(x =>
        {
            x.Post.Json(new StampedNumberRequest(3, 4)).ToUrl("/newtonsoft/stamped");
        });

        (await result.ReadAsTextAsync()).ShouldBe("7:newtonsoft");
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

public record StampedNumberRequest(int X, int Y)
{
    [JsonIgnore]
    public string StampedBy { get; init; } = string.Empty;
}

public static class StampedNumberEndpoint
{
    public static StampedNumberRequest Before(StampedNumberRequest request)
    {
        return request with { StampedBy = "newtonsoft" };
    }

    [WolverinePost("/newtonsoft/stamped")]
    public static string Post(StampedNumberRequest request)
    {
        return $"{request.X + request.Y}:{request.StampedBy}";
    }
}