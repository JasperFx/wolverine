using IntegrationTests;
using Marten;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.ApiExplorer;
using Microsoft.AspNetCore.Routing;
using Microsoft.AspNetCore.TestHost;
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
        await using var app = buildHybridHost();

        // Note: no app.StartAsync(). This is the first ApiExplorer read, which ASP.NET Core
        // caches for the lifetime of the host
        var relativePaths = readDescriptions(app)
            .Where(x => x.ActionDescriptor is WolverineActionDescriptor)
            .Select(x => x.RelativePath)
            .ToList();

        relativePaths.ShouldContain("validate2/customer");
        relativePaths.ShouldContain("validate2/user");
    }

    // GH-3421. Making Wolverine's own descriptions start-independent (GH-3373) left a hybrid host —
    // Wolverine HTTP endpoints alongside minimal API or MVC routes — describing only half of itself on
    // a pre-start read: Wolverine's routes present, everybody else's silently missing. A document that
    // lists a plausible subset of an application's routes reads as correct, so the omission surfaces far
    // downstream — a client team that cannot find an endpoint, a generated SDK quietly lacking one.
    [Fact]
    public async Task minimal_api_endpoints_are_described_before_the_host_starts()
    {
        await using var app = buildHybridHost();

        var descriptions = readDescriptions(app);

        var minimalApiRoutes = descriptions
            .Where(x => x.ActionDescriptor is not WolverineActionDescriptor)
            .Select(x => $"{x.HttpMethod} {x.RelativePath}")
            .ToList();

        // Mapped before MapWolverineEndpoints()...
        minimalApiRoutes.ShouldContain("GET minimal/hello");
        // ...and after it. Both land in the route builder's live DataSources collection, so the order
        // MapWolverineEndpoints() is called in must not matter.
        minimalApiRoutes.ShouldContain("POST minimal/goodbye");

        // And Wolverine's own endpoints are still described exactly once, not doubled by ASP.NET Core's
        // providers now that they can see the HttpGraph too
        descriptions.Count(x => x.RelativePath == "validate2/customer").ShouldBe(1);
    }

    // The route builder's data sources are published into ASP.NET Core's global collection ahead of the
    // description providers that read it. That collection is what UseEndpoints() fills at start, so a
    // double registration here would mean every endpoint in the application matching twice.
    [Fact]
    public async Task publishing_endpoints_early_does_not_duplicate_them_when_the_host_starts()
    {
        await using var app = buildHybridHost(useRealDatabase: true);

        // Forces the early publish, exactly as a build-time OpenAPI read or a monitoring snapshot would
        readDescriptions(app).ShouldNotBeEmpty();

        await app.StartAsync();

        var routes = app.Services.GetRequiredService<EndpointDataSource>().Endpoints
            .OfType<RouteEndpoint>()
            .Select(x => x.RoutePattern.RawText)
            .ToList();

        routes.Count(x => x == "/minimal/hello").ShouldBe(1);
        routes.Count(x => x == "/validate2/customer").ShouldBe(1);

        // The proof that matters: an ambiguous match would throw here rather than answer
        var client = app.GetTestServer().CreateClient();
        (await client.GetAsync("/minimal/hello")).EnsureSuccessStatusCode();

        await app.StopAsync();
    }

    // Endpoints are only ever published from the application's root route builder. MapWolverineEndpoints()
    // can also be called on a route group, and a group's DataSources are its *inner* sources — ASP.NET Core
    // publishes those itself, prefixed and carrying the group's conventions, through the GroupDataSource on
    // the outer builder. Publishing them a second time would register every endpoint in the group again,
    // un-prefixed and stripped of conventions such as the group's RequireAuthorization(), at a URL the
    // application never mapped.
    [Fact]
    public async Task wolverine_endpoints_mapped_into_a_route_group_are_registered_exactly_once()
    {
        await using var app = buildHybridHost(useRealDatabase: true, mapWolverineInto: app => app.MapGroup("/api"));

        // Forces the publish, exactly as a build-time OpenAPI read or a monitoring snapshot would
        readDescriptions(app).ShouldNotBeEmpty();

        await app.StartAsync();

        var routes = app.Services.GetRequiredService<EndpointDataSource>().Endpoints
            .OfType<RouteEndpoint>()
            .Select(x => x.RoutePattern.RawText)
            .ToList();

        routes.Count(x => x == "/api/validate2/customer").ShouldBe(1);
        routes.ShouldNotContain("/validate2/customer");

        await app.StopAsync();
    }

    // Wolverine reaches RouteOptions.EndpointDataSources — internal to Microsoft.AspNetCore.Routing —
    // to publish the host's endpoints before it starts. Fail here, in Wolverine's own build, if a future
    // ASP.NET Core moves it, rather than silently reverting to serving partial documents.
    [Fact]
    public void the_aspnetcore_endpoint_data_source_collection_is_still_reachable()
    {
        HostEndpointDataSources.EndpointDataSourcesProperty.ShouldNotBeNull();
    }

    private static WebApplication buildHybridHost(bool useRealDatabase = false,
        Func<WebApplication, IEndpointRouteBuilder>? mapWolverineInto = null)
    {
        var builder = WebApplication.CreateBuilder(new WebApplicationOptions { EnvironmentName = "Development" });
        builder.WebHost.UseTestServer();

        // "DifferentAssembly" also carries the Marten aggregate endpoints used by openapi_shape_tests, so
        // any host discovering it needs Marten registered to resolve the aggregates' id types. Unless the
        // host is actually going to be started, point it at an unreachable database (nothing listens on
        // port 9999) to keep the test free of infrastructure.
        builder.Services.AddMarten(opts =>
        {
            opts.Connection(useRealDatabase
                ? Servers.PostgresConnectionString
                : "Host=localhost;Port=9999;Database=does_not_exist;Username=nobody;Password=nobody;Timeout=2;Command Timeout=2");
        }).IntegrateWithWolverine();

        builder.Host.UseWolverine(opts =>
        {
            // Pin endpoint discovery to the small, isolated "DifferentAssembly" so the test is
            // deterministic and does not pick up the rest of the test suite's endpoints.
            opts.ApplicationAssembly = typeof(Validated2Endpoint).Assembly;
        });

        builder.Services.AddEndpointsApiExplorer();
        builder.Services.AddWolverineHttp();

        var app = builder.Build();

        app.MapGet("/minimal/hello", () => "Hello");
        (mapWolverineInto?.Invoke(app) ?? app).MapWolverineEndpoints();
        app.MapPost("/minimal/goodbye", () => "Goodbye");

        return app;
    }

    private static List<ApiDescription> readDescriptions(WebApplication app)
    {
        return app.Services.GetRequiredService<IApiDescriptionGroupCollectionProvider>()
            .ApiDescriptionGroups.Items
            .SelectMany(x => x.Items)
            .ToList();
    }
}
