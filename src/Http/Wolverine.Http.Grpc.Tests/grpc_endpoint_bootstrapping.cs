using Alba;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Shouldly;
using Wolverine.Http.Grpc;

namespace Wolverine.Http.Grpc.Tests;

/// <summary>
/// Integration tests for the Wolverine gRPC bootstrapping pipeline:
/// <c>AddWolverineGrpc()</c> + <c>MapWolverineGrpcEndpoints()</c>.
///
/// Each test spins up a self-contained <see cref="WebApplication"/> via
/// <see cref="AlbaHost"/> (the same pattern used in Wolverine.Http.Tests for
/// isolated bootstrapping scenarios) and inspects service registrations and
/// routing data sources — without making live gRPC network calls.
/// </summary>
public class grpc_endpoint_bootstrapping
{
    // Service registration tests
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

    // Route registration and discovery tests

    [Fact]
    public async Task map_wolverine_grpc_endpoints_starts_application_without_exception()
    {
        var builder = BuildMinimalApp();
        builder.Services.AddWolverineGrpc();

        // Should not throw during startup — gRPC routes for BootstrapAttributedGrpcService
        // are registered cleanly.
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
            // MapWolverineGrpcEndpoints should return the same IEndpointRouteBuilder
            // so that calls can be chained (e.g. app.MapWolverineGrpcEndpoints().MapHealthChecks(...)).
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
        // Tests constructor injection pattern (required for proto-first services that inherit proto-generated base classes)
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
        // Point Wolverine at the gRPC library assembly itself, which contains no user-defined
        // endpoint types.  MapWolverineGrpcEndpoints should log a warning and return without
        // throwing, leaving zero gRPC routes registered.
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
        // Tests the WolverineGrpcOptions.Assemblies configuration path
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
