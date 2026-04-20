using Grpc.AspNetCore.Server;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Shouldly;
using Xunit;

namespace Wolverine.Grpc.Tests;

/// <summary>
/// Regression tests for PR #2525 self-review §2.2 — without the marker guard,
/// repeat <see cref="WolverineGrpcExtensions.AddWolverineGrpc(IServiceCollection)"/>
/// invocations stack the <see cref="WolverineGrpcExceptionInterceptor"/> twice,
/// doubling exception translation and log output on every unhandled exception.
/// </summary>
public class add_wolverine_grpc_idempotency_tests
{
    [Fact]
    public void single_call_registers_exception_interceptor_exactly_once()
    {
        var services = new ServiceCollection();
        services.AddOptions();

        services.AddWolverineGrpc();

        var grpcOptions = services.BuildServiceProvider()
            .GetRequiredService<IOptions<GrpcServiceOptions>>().Value;

        grpcOptions.Interceptors.Count(r => r.Type == typeof(WolverineGrpcExceptionInterceptor))
            .ShouldBe(1);
    }

    [Fact]
    public void repeat_calls_do_not_stack_the_exception_interceptor()
    {
        var services = new ServiceCollection();
        services.AddOptions();

        services.AddWolverineGrpc();
        services.AddWolverineGrpc();
        services.AddWolverineGrpc();

        var grpcOptions = services.BuildServiceProvider()
            .GetRequiredService<IOptions<GrpcServiceOptions>>().Value;

        grpcOptions.Interceptors.Count(r => r.Type == typeof(WolverineGrpcExceptionInterceptor))
            .ShouldBe(1);
    }

    [Fact]
    public void repeat_calls_do_not_stack_the_grpc_graph_registration()
    {
        var services = new ServiceCollection();
        services.AddOptions();

        services.AddWolverineGrpc();
        services.AddWolverineGrpc();

        services.Count(d => d.ServiceType == typeof(GrpcGraph)).ShouldBe(1);
    }

    [Fact]
    public void marker_singleton_is_registered_after_first_call()
    {
        var services = new ServiceCollection();
        services.AddOptions();

        services.AddWolverineGrpc();

        services.Any(d => d.ServiceType == typeof(WolverineGrpcExtensions.WolverineGrpcMarker))
            .ShouldBeTrue();
    }

    [Fact]
    public void configure_overload_runs_callback_on_first_call()
    {
        var services = new ServiceCollection();
        services.AddOptions();

        var captured = false;
        services.AddWolverineGrpc(_ => captured = true);

        captured.ShouldBeTrue();
    }

    [Fact]
    public void configure_overload_runs_callback_on_repeat_calls_against_same_options_instance()
    {
        var services = new ServiceCollection();
        services.AddOptions();

        WolverineGrpcOptions? first = null;
        WolverineGrpcOptions? second = null;
        services.AddWolverineGrpc(o => first = o);
        services.AddWolverineGrpc(o => second = o);

        first.ShouldNotBeNull();
        second.ShouldNotBeNull();
        first.ShouldBeSameAs(second);
    }

    [Fact]
    public void options_singleton_is_resolvable_from_container()
    {
        var services = new ServiceCollection();
        services.AddOptions();
        services.AddWolverineGrpc();

        var resolved = services.BuildServiceProvider().GetService<WolverineGrpcOptions>();
        resolved.ShouldNotBeNull();
    }

    [Fact]
    public void repeat_calls_register_options_singleton_only_once()
    {
        var services = new ServiceCollection();
        services.AddOptions();
        services.AddWolverineGrpc();
        services.AddWolverineGrpc();
        services.AddWolverineGrpc();

        services.Count(d => d.ServiceType == typeof(WolverineGrpcOptions)).ShouldBe(1);
    }
}
