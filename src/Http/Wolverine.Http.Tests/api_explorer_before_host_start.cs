using Marten;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Mvc.ApiExplorer;
using Microsoft.Extensions.DependencyInjection;
using Shouldly;
using Wolverine.Http.Tests.DifferentAssembly.Validation;
using Wolverine.Marten;

namespace Wolverine.Http.Tests;

// ASP.NET Core caches the first ApiExplorer read for the lifetime of the host, and a host can
// be read before it starts (build-time OpenAPI generation, monitoring snapshots). Wolverine's
// descriptions must be complete on that first read, however early it happens.
public class api_explorer_before_host_start
{
    [Fact]
    public async Task api_descriptions_are_complete_before_the_host_starts()
    {
        var builder = WebApplication.CreateBuilder(new WebApplicationOptions { EnvironmentName = "Development" });

        // "DifferentAssembly" also carries the Marten aggregate endpoints used by openapi_shape_tests, so
        // any host discovering it needs Marten registered to resolve the aggregates' id types. Pointed at
        // an unreachable database (nothing listens on port 9999) to keep this test free of infrastructure.
        builder.Services.AddMarten(opts =>
        {
            opts.Connection(
                "Host=localhost;Port=9999;Database=does_not_exist;Username=nobody;Password=nobody;Timeout=2;Command Timeout=2");
        }).IntegrateWithWolverine();

        builder.Host.UseWolverine(opts =>
        {
            // Pin endpoint discovery to the small, isolated "DifferentAssembly" so the test is
            // deterministic and does not pick up the rest of the test suite's endpoints.
            opts.ApplicationAssembly = typeof(Validated2Endpoint).Assembly;
        });

        builder.Services.AddEndpointsApiExplorer();
        builder.Services.AddWolverineHttp();

        await using var app = builder.Build();
        app.MapWolverineEndpoints();

        // Note: no app.StartAsync(). This is the first ApiExplorer read, which ASP.NET Core
        // caches for the lifetime of the host
        var provider = app.Services.GetRequiredService<IApiDescriptionGroupCollectionProvider>();

        var relativePaths = provider.ApiDescriptionGroups.Items
            .SelectMany(x => x.Items)
            .Where(x => x.ActionDescriptor is WolverineActionDescriptor)
            .Select(x => x.RelativePath)
            .ToList();

        relativePaths.ShouldContain("validate2/customer");
        relativePaths.ShouldContain("validate2/user");
    }
}
