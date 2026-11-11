using Alba;
using JasperFx.CodeGeneration;
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace Wolverine.Http.Compat.Tests;

/// <summary>A Wolverine HTTP host over this assembly's endpoints, with no database and no broker.</summary>
public static class CompatHost
{
    public static async Task<IAlbaHost> For(
        Action<WolverineHttpOptions>? configureHttp = null,
        Action<IServiceCollection>? configureServices = null,
        Action<WebApplication>? configureApp = null)
    {
        var builder = WebApplication.CreateBuilder(Array.Empty<string>());
        builder.Environment.EnvironmentName = "Development";

        builder.Host.UseWolverine(opts =>
        {
            opts.Discovery.DisableConventionalDiscovery();
            opts.Discovery.IncludeAssembly(typeof(CompatHost).Assembly);
            opts.CodeGeneration.TypeLoadMode = TypeLoadMode.Dynamic;
        });
        builder.Services.AddWolverineHttp();
        configureServices?.Invoke(builder.Services);

        return await AlbaHost.For(builder, app =>
        {
            configureApp?.Invoke(app);
            app.MapWolverineEndpoints(opts =>
            {
                // Generate every endpoint at startup, so the tests can read the generated code
                opts.WarmUpRoutes = RouteWarmup.Eager;
                opts.UseDataAnnotationsValidationProblemDetailMiddleware();
                configureHttp?.Invoke(opts);
            });
        });
    }

    public static HttpChain Chain(this IAlbaHost host, string route)
    {
        var options = host.Services.GetRequiredService<WolverineHttpOptions>();
        return options.Endpoints!.Chains.Single(x => x.RoutePattern!.RawText == route);
    }
}
