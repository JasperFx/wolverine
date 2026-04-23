using JasperFx;
using JasperFx.CodeGeneration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Shouldly;
using Wolverine.Configuration;
using Wolverine.Middleware;
using Wolverine.Runtime;
using Xunit;

namespace Wolverine.Grpc.Tests.GrpcMiddlewareScoping;

/// <summary>
///     M15 §9.6 — pins the boundary between the message-handler middleware policy
///     (<see cref="IPolicies.AddMiddleware{T}"/>) and gRPC chains. The handler-only filter
///     baked into <c>WolverineOptions.Policies.cs:208-216</c> is the *only* thing keeping
///     handler middleware from leaking into <see cref="GrpcServiceChain"/>; if Phase-1 wiring
///     ever bypasses that filter (e.g. by registering <see cref="MiddlewarePolicy"/> as a
///     global <c>IChainPolicy</c> against gRPC chains too), users would suddenly see their
///     bus middleware run on every RPC call. Hence the explicit guard test here.
/// </summary>
[Collection("GrpcSerialTests")]
public class policy_leak_tests
{
    [Fact]
    public async Task ipolicies_add_middleware_does_not_attach_to_grpc_chain()
    {
        DynamicCodeBuilder.WithinCodegenCommand = true;
        try
        {
            using var host = await Host.CreateDefaultBuilder()
                .UseWolverine(opts =>
                {
                    opts.ApplicationAssembly = typeof(GreeterMiddlewareTestStub).Assembly;
                    opts.Policies.AddMiddleware<HandlerOnlyMiddleware>();
                })
                .ConfigureServices(services =>
                {
                    services.AddSingleton(new MiddlewareInvocationSink());
                    services.AddWolverineGrpc();
                })
                .StartAsync();

            var graph = host.Services.GetRequiredService<GrpcGraph>();
            var grpcOptions = host.Services.GetRequiredService<WolverineGrpcOptions>();
            graph.DiscoverServices(grpcOptions);
            var chain = graph.Chains.Single(c => c.StubType == typeof(GreeterMiddlewareTestStub));

            var options = host.Services.GetRequiredService<WolverineOptions>();
            var policy = options.FindOrCreateMiddlewarePolicy();
            var rules = options.CodeGeneration;
            var container = host.Services.GetRequiredService<IServiceContainer>();

            // Simulate the worst-case Phase-1 wiring: forcibly run the handler-only middleware
            // policy against a GrpcServiceChain. The HandlerChain-only filter at
            // WolverineOptions.Policies.cs:208-216 must reject the chain so no middleware lands.
            policy.Apply([chain], rules, container);

            chain.Middleware.ShouldBeEmpty(
                "IPolicies.AddMiddleware uses a HandlerChain-only filter — middleware registered "
                + "via the message-handler policy must never attach to a GrpcServiceChain. "
                + "gRPC users should use AddWolverineGrpc(g => g.AddMiddleware<T>()) instead.");
        }
        finally
        {
            DynamicCodeBuilder.WithinCodegenCommand = false;
        }
    }

}

/// <summary>
///     A trivial middleware whose only purpose is to be visible in the chain's middleware
///     list IF the filter ever broke. Method named per <see cref="MiddlewarePolicy.BeforeMethodNames"/>
///     so it is unambiguously a Before frame. Must be public + top-level — Wolverine rejects
///     nested or non-public middleware types in <see cref="MiddlewarePolicy.Application"/>'s
///     constructor.
/// </summary>
public sealed class HandlerOnlyMiddleware
{
    public static void Before(MiddlewareInvocationSink sink) => sink.Record("HandlerOnlyMiddleware.Before");
}

// ── Unit tests: WolverineGrpcOptions.AddPolicy ────────────────────────────────

/// <summary>
///     Pins the <see cref="WolverineGrpcOptions.AddPolicy"/> surface added in PR #2565.
///     These are pure unit tests — no host, no DI.
/// </summary>
public class wolverine_grpc_options_add_policy_tests
{
    [Fact]
    public void add_policy_generic_registers_instance_in_policies_list()
    {
        var opts = new WolverineGrpcOptions();
        opts.AddPolicy<RecordingGrpcChainPolicy>();

        opts.Policies.ShouldHaveSingleItem().ShouldBeOfType<RecordingGrpcChainPolicy>();
    }

    [Fact]
    public void add_policy_instance_overload_registers_in_policies_list()
    {
        var opts = new WolverineGrpcOptions();
        var policy = new RecordingGrpcChainPolicy();
        opts.AddPolicy(policy);

        opts.Policies.ShouldHaveSingleItem().ShouldBeSameAs(policy);
    }

    [Fact]
    public void add_policy_returns_self_for_fluent_chaining()
    {
        var opts = new WolverineGrpcOptions();
        opts.AddPolicy<RecordingGrpcChainPolicy>().ShouldBeSameAs(opts);
    }

    [Fact]
    public void multiple_policies_accumulate_in_order()
    {
        var opts = new WolverineGrpcOptions();
        var first = new RecordingGrpcChainPolicy();
        var second = new RecordingGrpcChainPolicy();

        opts.AddPolicy(first).AddPolicy(second);

        opts.Policies.Count.ShouldBe(2);
        opts.Policies[0].ShouldBeSameAs(first);
        opts.Policies[1].ShouldBeSameAs(second);
    }
}

// ── Integration: DiscoverServices calls all registered IGrpcChainPolicy ───────

/// <summary>
///     Verifies that <see cref="GrpcGraph.DiscoverServices"/> actually invokes every
///     <see cref="IGrpcChainPolicy"/> registered in <see cref="WolverineGrpcOptions.Policies"/>
///     and passes the correctly-typed chain lists to <c>Apply</c>.
/// </summary>
[Collection("GrpcSerialTests")]
public class igpc_chain_policy_discover_services_tests
{
    [Fact]
    public async Task registered_policy_is_called_during_discover_services()
    {
        DynamicCodeBuilder.WithinCodegenCommand = true;
        try
        {
            var policy = new RecordingGrpcChainPolicy();

            using var host = await Host.CreateDefaultBuilder()
                .UseWolverine(opts => opts.ApplicationAssembly = typeof(GreeterMiddlewareTestStub).Assembly)
                .ConfigureServices(services =>
                {
                    services.AddSingleton(new MiddlewareInvocationSink());
                    services.AddWolverineGrpc(opts => opts.AddPolicy(policy));
                })
                .StartAsync();

            var graph = host.Services.GetRequiredService<GrpcGraph>();
            var grpcOptions = host.Services.GetRequiredService<WolverineGrpcOptions>();
            graph.DiscoverServices(grpcOptions);

            policy.WasCalled.ShouldBeTrue(
                "IGrpcChainPolicy.Apply must be invoked during GrpcGraph.DiscoverServices");
        }
        finally
        {
            DynamicCodeBuilder.WithinCodegenCommand = false;
        }
    }

    [Fact]
    public async Task policy_receives_all_three_chain_type_lists_with_discovered_chains()
    {
        DynamicCodeBuilder.WithinCodegenCommand = true;
        try
        {
            var policy = new RecordingGrpcChainPolicy();

            using var host = await Host.CreateDefaultBuilder()
                .UseWolverine(opts => opts.ApplicationAssembly = typeof(GreeterMiddlewareTestStub).Assembly)
                .ConfigureServices(services =>
                {
                    services.AddSingleton(new MiddlewareInvocationSink());
                    services.AddWolverineGrpc(opts => opts.AddPolicy(policy));
                })
                .StartAsync();

            var graph = host.Services.GetRequiredService<GrpcGraph>();
            var grpcOptions = host.Services.GetRequiredService<WolverineGrpcOptions>();
            graph.DiscoverServices(grpcOptions);

            // The test assembly contains stubs from all three discovery paths.
            policy.ProtoFirstCount.ShouldBeGreaterThan(0,
                "policy must receive the proto-first chains list populated with discovered stubs");
            policy.CodeFirstCount.ShouldBeGreaterThan(0,
                "policy must receive the code-first chains list populated with discovered contracts");
            policy.HandWrittenCount.ShouldBeGreaterThan(0,
                "policy must receive the hand-written chains list populated with discovered service classes");
        }
        finally
        {
            DynamicCodeBuilder.WithinCodegenCommand = false;
        }
    }
}

// ── Integration: AddMiddleware<T> with a custom filter predicate ──────────────

/// <summary>
///     Pins the <c>filter</c> parameter of <see cref="WolverineGrpcOptions.AddMiddleware{T}"/>.
///     The default (no filter) attaches to every gRPC chain; a custom predicate must limit
///     attachment to only the chains that match.
/// </summary>
[Collection("GrpcSerialTests")]
public class add_middleware_custom_filter_tests
{
    [Fact]
    public async Task always_false_filter_prevents_middleware_from_attaching_to_any_chain()
    {
        DynamicCodeBuilder.WithinCodegenCommand = true;
        try
        {
            using var host = await Host.CreateDefaultBuilder()
                .UseWolverine(opts => opts.ApplicationAssembly = typeof(GreeterMiddlewareTestStub).Assembly)
                .ConfigureServices(services =>
                {
                    services.AddSingleton(new MiddlewareInvocationSink());
                    services.AddWolverineGrpc(opts =>
                        opts.AddMiddleware<GrpcFilterScopeMiddleware>(filter: _ => false));
                })
                .StartAsync();

            var graph = host.Services.GetRequiredService<GrpcGraph>();
            var grpcOptions = host.Services.GetRequiredService<WolverineGrpcOptions>();
            graph.DiscoverServices(grpcOptions);

            graph.Chains.ShouldAllBe(c => c.Middleware.Count == 0,
                "always-false filter must not attach middleware to any proto-first chain");
            graph.CodeFirstChains.ShouldAllBe(c => c.Middleware.Count == 0,
                "always-false filter must not attach middleware to any code-first chain");
            graph.HandWrittenChains.ShouldAllBe(c => c.Middleware.Count == 0,
                "always-false filter must not attach middleware to any hand-written chain");
        }
        finally
        {
            DynamicCodeBuilder.WithinCodegenCommand = false;
        }
    }

    [Fact]
    public async Task proto_first_only_filter_does_not_attach_to_code_first_or_hand_written_chains()
    {
        DynamicCodeBuilder.WithinCodegenCommand = true;
        try
        {
            using var host = await Host.CreateDefaultBuilder()
                .UseWolverine(opts => opts.ApplicationAssembly = typeof(GreeterMiddlewareTestStub).Assembly)
                .ConfigureServices(services =>
                {
                    services.AddSingleton(new MiddlewareInvocationSink());
                    services.AddWolverineGrpc(opts =>
                        opts.AddMiddleware<GrpcFilterScopeMiddleware>(
                            filter: c => c is GrpcServiceChain));
                })
                .StartAsync();

            var graph = host.Services.GetRequiredService<GrpcGraph>();
            var grpcOptions = host.Services.GetRequiredService<WolverineGrpcOptions>();
            graph.DiscoverServices(grpcOptions);

            graph.Chains.ShouldAllBe(c => c.Middleware.Count > 0,
                "proto-first-only filter must attach to every proto-first chain");
            graph.CodeFirstChains.ShouldAllBe(c => c.Middleware.Count == 0,
                "proto-first-only filter must not attach to code-first chains");
            graph.HandWrittenChains.ShouldAllBe(c => c.Middleware.Count == 0,
                "proto-first-only filter must not attach to hand-written chains");
        }
        finally
        {
            DynamicCodeBuilder.WithinCodegenCommand = false;
        }
    }
}

// ── Support types ─────────────────────────────────────────────────────────────

/// <summary>
///     Records which chain-type lists it received so integration tests can assert
///     that <c>IGrpcChainPolicy.Apply</c> was invoked with the right populations.
/// </summary>
public sealed class RecordingGrpcChainPolicy : IGrpcChainPolicy
{
    public bool WasCalled { get; private set; }
    public int ProtoFirstCount { get; private set; }
    public int CodeFirstCount { get; private set; }
    public int HandWrittenCount { get; private set; }

    public void Apply(
        IReadOnlyList<GrpcServiceChain> protoFirstChains,
        IReadOnlyList<CodeFirstGrpcServiceChain> codeFirstChains,
        IReadOnlyList<HandWrittenGrpcServiceChain> handWrittenChains,
        GenerationRules rules,
        IServiceContainer container)
    {
        WasCalled = true;
        ProtoFirstCount = protoFirstChains.Count;
        CodeFirstCount = codeFirstChains.Count;
        HandWrittenCount = handWrittenChains.Count;
    }
}

/// <summary>
///     Trivial no-op middleware used by <see cref="add_middleware_custom_filter_tests"/> to
///     verify that the custom filter predicate is respected. No injected parameters so no
///     DI container wiring is needed.
/// </summary>
public sealed class GrpcFilterScopeMiddleware
{
    public static void Before() { }
}
