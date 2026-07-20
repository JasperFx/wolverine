using Marten;
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.DependencyInjection;
using Shouldly;
using Wolverine.Http.Tests.DifferentAssembly.Validation;
using Wolverine.Marten;

namespace Wolverine.Http.Tests;

// HTTP endpoint discovery honours the same namespace splits that HandlerDiscovery offers via
// CustomizeHandlerDiscovery(q => q.Excludes.InNamespace(...)) — the handler-side counterpart tracked
// in GH-3371. WolverineHttpOptions.CustomizeHttpEndpointDiscovery feeds the excludes that
// HttpChainSource owns, so an endpoint in an excluded namespace of a scanned assembly is dropped.
// The two facts pinned here: with no filter both namespaces are discovered; with an exclusion the
// excluded namespace is dropped while its sibling still resolves.
public class http_endpoint_discovery_filter
{
    private const string IncludedRoute = "/discovery-filter/included";
    private const string ExcludedRoute = "/discovery-filter/excluded";
    private const string ExcludedNamespace = "Wolverine.Http.Tests.DifferentAssembly.DiscoveryFilter.Excluded";

    [Fact]
    public async Task by_default_both_namespaces_are_discovered()
    {
        var endpoints = await DiscoverEndpointsAsync();

        endpoints.ChainFor("GET", IncludedRoute).ShouldNotBeNull();
        endpoints.ChainFor("GET", ExcludedRoute).ShouldNotBeNull();
    }

    [Fact]
    public async Task excluded_namespace_endpoint_is_not_registered_when_filtered()
    {
        var endpoints = await DiscoverEndpointsAsync(opts =>
            opts.CustomizeHttpEndpointDiscovery(q => q.Excludes.InNamespace(ExcludedNamespace)));

        // The sibling namespace still resolves...
        endpoints.ChainFor("GET", IncludedRoute).ShouldNotBeNull();

        // ...but the excluded-namespace endpoint, which the default discovered above, is gone.
        endpoints.ChainFor("GET", ExcludedRoute).ShouldBeNull();
    }

    // Boots a minimal, database-less host whose endpoint discovery is pinned to the small, isolated
    // "DifferentAssembly" (the same bootstrap generate_openapi_without_database uses), then returns the
    // built HttpGraph without ever starting the host. No app.StartAsync() and the Marten connection is
    // unreachable, so nothing here touches a database.
    private static async Task<HttpGraph> DiscoverEndpointsAsync(Action<WolverineHttpOptions>? configure = null)
    {
        var builder = WebApplication.CreateBuilder();

        builder.Services
            .AddMarten(opts =>
            {
                opts.Connection(
                    "Host=localhost;Port=9999;Database=does_not_exist;Username=nobody;Password=nobody;Timeout=2;Command Timeout=2");
            })
            .IntegrateWithWolverine();

        builder.Host.UseWolverine(opts =>
        {
            opts.ApplicationAssembly = typeof(Validated2Endpoint).Assembly;
            opts.Policies.AutoApplyTransactions();
            opts.Policies.UseDurableLocalQueues();
        });

        builder.Services.AddWolverineHttp();

        await using var app = builder.Build();
        app.MapWolverineEndpoints(configure);

        return app.Services.GetRequiredService<WolverineHttpOptions>().Endpoints!;
    }
}
