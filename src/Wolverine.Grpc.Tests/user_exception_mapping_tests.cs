using Grpc.Core;
using Grpc.Net.Client;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using PingPongWithGrpc.Ponger;
using ProtoBuf.Grpc.Client;
using ProtoBuf.Grpc.Server;
using Shouldly;
using Xunit;

namespace Wolverine.Grpc.Tests;

// ── Unit tests for TryMapException resolution logic ──────────────────────────

public class wolverine_grpc_options_exception_mapping_tests
{
    [Fact]
    public void returns_null_when_no_mappings_registered()
    {
        var opts = new WolverineGrpcOptions();
        opts.TryMapException(new ArgumentException("x")).ShouldBeNull();
    }

    [Fact]
    public void exact_type_match_returns_registered_code()
    {
        var opts = new WolverineGrpcOptions();
        opts.MapException<ArgumentException>(StatusCode.InvalidArgument);

        opts.TryMapException(new ArgumentException("x")).ShouldBe(StatusCode.InvalidArgument);
    }

    [Fact]
    public void base_type_mapping_matches_derived_exception()
    {
        var opts = new WolverineGrpcOptions();
        opts.MapException<Exception>(StatusCode.Unknown);

        opts.TryMapException(new KeyNotFoundException("x")).ShouldBe(StatusCode.Unknown);
    }

    [Fact]
    public void more_derived_type_wins_over_base_type()
    {
        var opts = new WolverineGrpcOptions();
        opts.MapException<Exception>(StatusCode.Unknown);
        opts.MapException<ArgumentException>(StatusCode.InvalidArgument);

        opts.TryMapException(new ArgumentNullException("p"))
            .ShouldBe(StatusCode.InvalidArgument,
                "ArgumentNullException inherits ArgumentException — its mapping wins over Exception");
    }

    [Fact]
    public void later_registration_wins_for_same_type()
    {
        var opts = new WolverineGrpcOptions();
        opts.MapException<ArgumentException>(StatusCode.Internal);
        opts.MapException<ArgumentException>(StatusCode.InvalidArgument);

        opts.TryMapException(new ArgumentException("x")).ShouldBe(StatusCode.InvalidArgument);
    }

    [Fact]
    public void unregistered_exception_returns_null()
    {
        var opts = new WolverineGrpcOptions();
        opts.MapException<ArgumentException>(StatusCode.InvalidArgument);

        opts.TryMapException(new TimeoutException()).ShouldBeNull();
    }

    [Fact]
    public void non_exception_type_throws_argument_exception()
    {
        var opts = new WolverineGrpcOptions();
        Should.Throw<ArgumentException>(() => opts.MapException(typeof(string), StatusCode.Internal));
    }

    [Fact]
    public void map_exception_returns_self_for_fluent_chaining()
    {
        var opts = new WolverineGrpcOptions();
        opts.MapException<ArgumentException>(StatusCode.InvalidArgument)
            .MapException<TimeoutException>(StatusCode.DeadlineExceeded)
            .ShouldBeSameAs(opts);
    }
}

// ── Integration test: full interceptor pipeline respects user mapping ─────────

public class user_exception_mapping_integration_tests : IAsyncLifetime
{
    private WebApplication? _app;
    private GrpcChannel? _channel;

    public async Task InitializeAsync()
    {
        var builder = WebApplication.CreateBuilder([]);
        builder.WebHost.UseTestServer();

        builder.Host.UseWolverine(opts =>
        {
            opts.ApplicationAssembly = typeof(PingGrpcService).Assembly;
            opts.Discovery.IncludeAssembly(typeof(user_exception_mapping_integration_tests).Assembly);
        });

        builder.Services.AddCodeFirstGrpc();
        builder.Services.AddWolverineGrpc(opts =>
        {
            // Override default: TimeoutException → ResourceExhausted instead of DeadlineExceeded
            opts.MapException<TimeoutException>(StatusCode.ResourceExhausted);
            // Custom domain exception type
            opts.MapException<DomainValidationException>(StatusCode.FailedPrecondition);
        });

        builder.Services.AddSingleton<PingTracker>();

        _app = builder.Build();
        _app.UseRouting();
        _app.MapGrpcService<FaultingGrpcService>();
        await _app.StartAsync();

        var handler = _app.GetTestServer().CreateHandler();
        _channel = GrpcChannel.ForAddress("http://localhost", new GrpcChannelOptions { HttpHandler = handler });
    }

    public async Task DisposeAsync()
    {
        _channel?.Dispose();
        if (_app != null)
        {
            await _app.StopAsync();
            await _app.DisposeAsync();
        }
    }

    [Fact]
    public async Task user_mapping_overrides_default_table_for_timeout()
    {
        var client = _channel!.CreateGrpcService<IFaultingService>();

        var ex = await Should.ThrowAsync<RpcException>(
            () => client.Throw(new FaultCodeFirstRequest { Kind = "timeout" }));

        ex.StatusCode.ShouldBe(StatusCode.ResourceExhausted,
            "user registered TimeoutException → ResourceExhausted, which should win over default DeadlineExceeded");
    }

    [Fact]
    public async Task user_mapping_handles_custom_domain_exception()
    {
        var client = _channel!.CreateGrpcService<IFaultingService>();

        var ex = await Should.ThrowAsync<RpcException>(
            () => client.Throw(new FaultCodeFirstRequest { Kind = "domain-validation" }));

        ex.StatusCode.ShouldBe(StatusCode.FailedPrecondition,
            "DomainValidationException was registered as FailedPrecondition");
    }

    [Fact]
    public async Task unmapped_exception_still_uses_default_table()
    {
        var client = _channel!.CreateGrpcService<IFaultingService>();

        var ex = await Should.ThrowAsync<RpcException>(
            () => client.Throw(new FaultCodeFirstRequest { Kind = "key" }));

        ex.StatusCode.ShouldBe(StatusCode.NotFound,
            "KeyNotFoundException has no user mapping — falls through to default AIP-193 table");
    }
}

/// <summary>Domain exception used by the user-mapping integration tests.</summary>
public sealed class DomainValidationException(string message) : Exception(message);
