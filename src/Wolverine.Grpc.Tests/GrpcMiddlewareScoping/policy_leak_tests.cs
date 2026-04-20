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
            graph.DiscoverServices();
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
