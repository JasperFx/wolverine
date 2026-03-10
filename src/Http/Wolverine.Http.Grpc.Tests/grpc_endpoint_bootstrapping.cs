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
    // ---------------------------------------------------------------------------
    // AddWolverineGrpc — service registration
    // ---------------------------------------------------------------------------

    [Fact]
    public async Task add_wolverine_grpc_registers_wolverine_grpc_options_as_singleton()
    {
        var builder = BuildMinimalApp();
        builder.Services.AddWolverineGrpc();

        await using var host = await AlbaHost.For(builder, _ => { });

        // The same WolverineGrpcOptions instance should be resolved every time
        // (singleton lifetime).
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
        // BootstrapAttributedGrpcService is decorated with [WolverineGrpcService] and implements
        // IBootstrapPingContract which declares PingAsync.  After MapWolverineGrpcEndpoints() the
        // ASP.NET Core routing table should contain at least one RouteEndpoint whose pattern
        // includes the contract name.
        //
        // protobuf-net.Grpc derives the gRPC service name from the SERVICE CONTRACT INTERFACE
        // (not from the implementation class), strips the leading 'I', and strips any "Async"
        // suffix from method names.  The expected route pattern is therefore:
        //   /{Namespace}.BootstrapPingContract/Ping
        //
        // Note: WebApplication is used directly here (instead of AlbaHost) because the DI-registered
        // CompositeEndpointDataSource is populated from app.DataSources only after app.StartAsync()
        // is called on the real host.  AlbaHost's TestServer wires up the pipeline differently and
        // host.Services.GetRequiredService<EndpointDataSource>() remains empty in that context.
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

            // "BootstrapPingContract" is the interface name IBootstrapPingContract without the 'I'.
            routeEndpoints
                .Any(e => e.RoutePattern.RawText?.Contains(
                    "BootstrapPingContract", StringComparison.OrdinalIgnoreCase) == true)
                .ShouldBeTrue("Expected a gRPC route containing 'BootstrapPingContract' to be " +
                              "registered for IBootstrapPingContract / BootstrapAttributedGrpcService");
        }
        finally
        {
            await app.StopAsync();
        }
    }

    [Fact]
    public async Task map_wolverine_grpc_endpoints_registers_route_for_constructor_injected_service()
    {
        // BootstrapCtorInjectedGrpcService uses [WolverineGrpcService] + constructor injection of
        // IMessageBus WITHOUT inheriting WolverineGrpcEndpointBase.  This is the code-first
        // constructor-injection pattern (and also the required pattern for proto-first services).
        // MapWolverineGrpcEndpoints() must discover and map it via the attribute path.
        //
        // protobuf-net.Grpc derives the route prefix from the SERVICE CONTRACT INTERFACE name
        // (strips the leading 'I'), so the expected route pattern for IBootstrapCtorContract is:
        //   /{Namespace}.BootstrapCtorContract/CtorPing   (method "CtorPingAsync" → "CtorPing")
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
                .ShouldBeTrue(
                    "Expected a gRPC route containing 'BootstrapCtorContract' to be registered " +
                    "for IBootstrapCtorContract / BootstrapCtorInjectedGrpcService, which uses " +
                    "[WolverineGrpcService] + constructor injection (no WolverineGrpcEndpointBase)");
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

        // Must not throw even when no endpoint types are found.
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
        // The test assembly contains BootstrapAttributedGrpcService.
        // Configuring WolverineGrpcOptions.Assemblies to include it while the
        // application assembly is the gRPC library (which has no user-defined endpoints) exercises
        // the assembly-merging path in MapWolverineGrpcEndpoints.
        //
        // WebApplication is used directly for route inspection (see the other route test for why).
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
                .ShouldBeTrue("Expected 'BootstrapPingContract' route to be registered " +
                              "from the additionally-scanned test assembly (protobuf-net strips the 'I' " +
                              "from IBootstrapPingContract to form the gRPC service route prefix)");
        }
        finally
        {
            await app.StopAsync();
        }
    }

    // ---------------------------------------------------------------------------
    // WolverineGrpcOptions — configuration surface
    // ---------------------------------------------------------------------------

    [Fact]
    public void wolverine_grpc_options_assemblies_collection_starts_empty()
    {
        var options = new WolverineGrpcOptions();
        options.Assemblies.ShouldBeEmpty();
    }

    [Fact]
    public void wolverine_grpc_options_assemblies_collection_accepts_added_assemblies()
    {
        var options = new WolverineGrpcOptions();
        var assembly = typeof(grpc_endpoint_bootstrapping).Assembly;

        options.Assemblies.Add(assembly);

        options.Assemblies.ShouldContain(assembly);
        options.Assemblies.Count.ShouldBe(1);
    }

    // ---------------------------------------------------------------------------
    // Helper
    // ---------------------------------------------------------------------------

    /// <summary>
    /// Creates a minimal <see cref="WebApplicationBuilder"/> that uses the test assembly as the
    /// Wolverine application assembly and disables conventional handler discovery so that the
    /// test types (which are gRPC service classes, not Wolverine handlers) do not confuse
    /// the handler compiler.
    /// </summary>
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
