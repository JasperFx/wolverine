using Alba;
using IntegrationTests;
using Marten;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.DependencyInjection;
using Wolverine.Marten;

namespace Wolverine.Http.Tests;

public class named_route_parameter_binding
{
    private static async Task<IAlbaHost> hostFor(Type endpointType)
    {
        var builder = WebApplication.CreateBuilder([]);

        builder.Services.AddMarten(opts =>
        {
            opts.Connection(Servers.PostgresConnectionString);
            opts.DisableNpgsqlLogging = true;
        }).IntegrateWithWolverine();

        builder.Host.UseWolverine(opts =>
        {
            opts.Discovery.DisableConventionalDiscovery().IncludeType(endpointType);
            opts.ApplicationAssembly = typeof(named_route_parameter_binding).Assembly;
        });

        builder.Services.AddWolverineHttp();

        return await AlbaHost.For(builder, app => app.MapWolverineEndpoints());
    }

    [Fact]
    public async Task bind_kebab_cased_route_argument_to_a_method_parameter()
    {
        await using var host = await hostFor(typeof(NamedRouteEndpoints));

        await host.Scenario(x =>
        {
            x.Get.Url("/named-route/direct/42");
            x.ContentShouldBe("42");
        });
    }

    [Fact]
    public async Task bind_kebab_cased_route_argument_to_a_string_method_parameter()
    {
        await using var host = await hostFor(typeof(NamedRouteEndpoints));

        await host.Scenario(x =>
        {
            x.Get.Url("/named-route/direct-string/Aubrey");
            x.ContentShouldBe("Aubrey");
        });
    }

    [Fact]
    public async Task bind_kebab_cased_route_argument_through_as_parameters_with_a_body()
    {
        await using var host = await hostFor(typeof(NamedRouteEndpoints));

        await host.Scenario(x =>
        {
            x.Post.Json(new NamedRouteBody("Maverick")).ToUrl("/named-route/as-parameters/11");
            x.ContentShouldBe("11:Maverick");
        });
    }

}

public record NamedRouteBody(string Name);

public record NamedRouteRequest([FromRoute(Name = "my-id")] int Id, [FromBody] NamedRouteBody Body);

public static class NamedRouteEndpoints
{
    [WolverineGet("/named-route/direct/{my-id}")]
    public static string Direct([FromRoute(Name = "my-id")] int id)
    {
        return id.ToString();
    }

    [WolverineGet("/named-route/direct-string/{my-name}")]
    public static string DirectString([FromRoute(Name = "my-name")] string name)
    {
        return name;
    }

    [WolverinePost("/named-route/as-parameters/{my-id}")]
    public static string WithBody([AsParameters] NamedRouteRequest request)
    {
        return $"{request.Id}:{request.Body.Name}";
    }
}

