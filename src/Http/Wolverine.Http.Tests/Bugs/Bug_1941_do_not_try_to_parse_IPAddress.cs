using System.Net;
using Alba;
using IntegrationTests;
using Marten;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Wolverine.Marten;

namespace Wolverine.Http.Tests.Bugs;

public class Bug_1941_do_not_try_to_parse_IPAddress
{
    [Fact]
    public async Task pipe_ipaddress_from_load_to_main_handle_method()
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
            opts.Discovery.DisableConventionalDiscovery().IncludeType(typeof(IpEndpoint));
            opts.ApplicationAssembly = GetType().Assembly;
        });

        builder.Services.AddWolverineHttp();

        // This is using Alba, which uses WebApplicationFactory under the covers
        await using var host = await AlbaHost.For(builder, app =>
        {
            app.MapWolverineEndpoints(x =>
            {
                x.AddMiddleware(typeof(RequestIpMiddleware));
            });
        });

        var result = await host.Scenario(x =>
        {
            x.Post.Json(new IpRequest { Name = "Jeremy", Age = 51 }).ToUrl("/ip");
        });
    }
}

public class RequestIpMiddleware
{
    public static (IResult, IPAddress?) LoadAsync(HttpContext httpContext)
    {
        var ip = httpContext.Connection.RemoteIpAddress;
        if (ip != null && ip.IsIPv4MappedToIPv6)
        {
            ip = ip.MapToIPv4();
        }
        return (WolverineContinue.Result(), ip);
    }
}

public class IpRequest
{
    public string Name { get; set; }
    public int Age { get; set; }
}

public static class IpEndpoint
{

    [WolverinePost("/ip")]
    public static string Get(IpRequest request, IPAddress? address)
    {
        return address?.ToString() ?? "no address";
    }
}