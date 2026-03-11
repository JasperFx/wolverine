using Alba;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Shouldly;
using Wolverine.Http.Grpc;

namespace Wolverine.Http.Grpc.Tests;

public class grpc_endpoint_bootstrapping
{

    [Fact]
    public async Task add_wolverine_grpc_registers_wolverine_grpc_options_as_singleton()
    {
        var builder = BuildMinimalApp();
        builder.Services.AddWolverineGrpc();

        await using var host = await AlbaHost.For(builder, _ => { });

        var first = host.Services.GetRequiredService<WolverineGrpcOptions>();
        var second = host.Services.GetRequiredService<WolverineGrpcOptions>();
        first.ShouldBeSameAs(second);
    }

    [Fact]
    public async Task add_wolverine_grpc_applies_configure_callback_to_options()
    {
        var extraAssembly = typeof(grpc_endpoint_bootstrapping).Assembly;

        var builder = BuildMinimalApp();
        builder.Services.AddWolverineGrpc(opts =>
        {
            opts.Assemblies.Add(extraAssembly);
        });

        await using var host = await AlbaHost.For(builder, _ => { });

        var options = host.Services.GetRequiredService<WolverineGrpcOptions>();
        options.Assemblies.ShouldContain(extraAssembly);
    }

    [Fact]
    public async Task add_wolverine_grpc_without_configure_callback_leaves_assemblies_empty()
    {
        var builder = BuildMinimalApp();
        builder.Services.AddWolverineGrpc();

        await using var host = await AlbaHost.For(builder, _ => { });

        var options = host.Services.GetRequiredService<WolverineGrpcOptions>();
        options.Assemblies.ShouldBeEmpty();
    }

    // ---------------------------------------------------------------------------
    // MapWolverineGrpcEndpoints — routing and startup
    // ---------------------------------------------------------------------------

    [Fact]
    public async Task map_wolverine_grpc_endpoints_starts_application_without_exception()
    {
        var builder = BuildMinimalApp();
        builder.Services.AddWolverineGrpc();

        await using var host = await AlbaHost.For(builder, app =>
        {
            app.UseRouting();
            app.MapWolverineGrpcEndpoints();
        });

        host.ShouldNotBeNull();
    }

    [Fact]
    public async Task map_wolverine_grpc_endpoints_returns_endpoint_route_builder_for_chaining()
    {
        var builder = BuildMinimalApp();
        builder.Services.AddWolverineGrpc();

        IEndpointRouteBuilder? returned = null;

        await using var host = await AlbaHost.For(builder, app =>
        {
            app.UseRouting();
            returned = app.MapWolverineGrpcEndpoints();
        });

        returned.ShouldNotBeNull();
    }

    [Fact]
    public async Task map_wolverine_grpc_endpoints_registers_grpc_route_for_discovered_service()
    {
        // WebApplication is used directly here because AlbaHost's TestServer wires up the pipeline
        // differently and host.Services.GetRequiredService<EndpointDataSource>() remains empty in that context.
        // protobuf-net.Grpc derives the gRPC service name from the SERVICE CONTRACT INTERFACE,
        // strips the leading 'I', and strips any "Async" suffix from method names.
        var builder = BuildMinimalApp();
        builder.Services.AddWolverineGrpc();

        await using var app = builder.Build();
        app.UseRouting();
        app.MapWolverineGrpcEndpoints();
        await app.StartAsync();

        try
        {
            var dataSource = app.Services.GetRequiredService<EndpointDataSource>();
            var routeEndpoints = dataSource.Endpoints.OfType<RouteEndpoint>().ToList();

            routeEndpoints.ShouldNotBeEmpty("At least one gRPC route must be registered");

            routeEndpoints
                .Any(e => e.RoutePattern.RawText?.Contains(
                    "BootstrapPingContract", StringComparison.OrdinalIgnoreCase) == true)
                .ShouldBeTrue();
        }
        finally
        {
            await app.StopAsync();
        }
    }

    [Fact]
    public async Task map_wolverine_grpc_endpoints_registers_route_for_constructor_injected_service()
    {
        var builder = BuildMinimalApp();
        builder.Services.AddWolverineGrpc();

        await using var app = builder.Build();
        app.UseRouting();
        app.MapWolverineGrpcEndpoints();
        await app.StartAsync();

        try
        {
            var dataSource = app.Services.GetRequiredService<EndpointDataSource>();
            var routeEndpoints = dataSource.Endpoints.OfType<RouteEndpoint>().ToList();

            routeEndpoints.ShouldNotBeEmpty("At least one gRPC route must be registered");

            routeEndpoints
                .Any(e => e.RoutePattern.RawText?.Contains(
                    "BootstrapCtorContract", StringComparison.OrdinalIgnoreCase) == true)
                .ShouldBeTrue();
        }
        finally
        {
            await app.StopAsync();
        }
    }

    [Fact]
    public async Task map_wolverine_grpc_endpoints_handles_no_discovered_types_gracefully()
    {
        var builder = WebApplication.CreateBuilder([]);
        builder.Host.UseWolverine(opts =>
        {
            opts.ApplicationAssembly = typeof(WolverineGrpcEndpointBase).Assembly;
            opts.Discovery.DisableConventionalDiscovery();
        });
        builder.Services.AddWolverineGrpc();

        await using var host = await AlbaHost.For(builder, app =>
        {
            app.UseRouting();
            app.MapWolverineGrpcEndpoints();
        });

        host.ShouldNotBeNull();
    }

    [Fact]
    public async Task map_wolverine_grpc_endpoints_scans_additional_assemblies_from_options()
    {
        var testAssembly = typeof(grpc_endpoint_bootstrapping).Assembly;

        var builder = WebApplication.CreateBuilder([]);
        builder.Host.UseWolverine(opts =>
        {
            opts.ApplicationAssembly = typeof(WolverineGrpcEndpointBase).Assembly;
            opts.Discovery.DisableConventionalDiscovery();
        });
        builder.Services.AddWolverineGrpc(opts =>
        {
            opts.Assemblies.Add(testAssembly);
        });

        await using var app = builder.Build();
        app.UseRouting();
        app.MapWolverineGrpcEndpoints();
        await app.StartAsync();

        try
        {
            var dataSource = app.Services.GetRequiredService<EndpointDataSource>();
            var routeEndpoints = dataSource.Endpoints.OfType<RouteEndpoint>().ToList();

            routeEndpoints
                .Any(e => e.RoutePattern.RawText?.Contains(
                    "BootstrapPingContract", StringComparison.OrdinalIgnoreCase) == true)
                .ShouldBeTrue();
        }
        finally
        {
            await app.StopAsync();
        }
    }

    private static WebApplicationBuilder BuildMinimalApp()
    {
        var builder = WebApplication.CreateBuilder([]);

        builder.Host.UseWolverine(opts =>
        {
            opts.ApplicationAssembly = typeof(grpc_endpoint_bootstrapping).Assembly;
            opts.Discovery.DisableConventionalDiscovery();
        });

        return builder;
    }
}
