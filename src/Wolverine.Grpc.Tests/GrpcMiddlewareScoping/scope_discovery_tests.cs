using JasperFx;
using JasperFx.CodeGeneration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Shouldly;
using Xunit;

namespace Wolverine.Grpc.Tests.GrpcMiddlewareScoping;

/// <summary>
///     Verifies the M15 Phase-0 discovery: <see cref="GrpcServiceChain.DiscoveredBefores"/> and
///     <see cref="GrpcServiceChain.DiscoveredAfters"/> walk the user's stub type and honor
///     <see cref="Wolverine.Attributes.MiddlewareScoping"/>. These tests pin the contract that
///     Phase-1 codegen will rely on — get this wrong and the eventual weaving will silently
///     attach (or skip) the wrong methods.
/// </summary>
public class scope_discovery_tests
{
    [Fact]
    public async Task discovers_anywhere_and_grpc_scoped_befores_on_stub_type()
    {
        var chain = await DiscoverStubChainAsync();

        var names = chain.DiscoveredBefores.Select(m => m.Name).ToArray();

        names.ShouldContain(nameof(GreeterMiddlewareTestStub.BeforeAnywhere));
        names.ShouldContain(nameof(GreeterMiddlewareTestStub.BeforeGrpc));
    }

    [Fact]
    public async Task does_not_discover_message_handlers_scoped_befores_on_stub_type()
    {
        var chain = await DiscoverStubChainAsync();

        var names = chain.DiscoveredBefores.Select(m => m.Name).ToArray();

        names.ShouldNotContain(nameof(GreeterMiddlewareTestStub.BeforeMessageHandlers),
            "[WolverineBefore(MessageHandlers)] must not leak into a gRPC chain's discovered middleware");
    }

    [Fact]
    public async Task discovers_anywhere_and_grpc_scoped_afters_on_stub_type()
    {
        var chain = await DiscoverStubChainAsync();

        var names = chain.DiscoveredAfters.Select(m => m.Name).ToArray();

        names.ShouldContain(nameof(GreeterMiddlewareTestStub.AfterAnywhere));
        names.ShouldContain(nameof(GreeterMiddlewareTestStub.AfterGrpc));
    }

    [Fact]
    public async Task does_not_discover_message_handlers_scoped_afters_on_stub_type()
    {
        var chain = await DiscoverStubChainAsync();

        var names = chain.DiscoveredAfters.Select(m => m.Name).ToArray();

        names.ShouldNotContain(nameof(GreeterMiddlewareTestStub.AfterMessageHandlers),
            "[WolverineAfter(MessageHandlers)] must not leak into a gRPC chain's discovered postprocessors");
    }

    [Fact]
    public async Task discovered_befores_are_ordinally_sorted_for_deterministic_codegen()
    {
        // Reflection's GetMethods() order is unspecified — if Phase-1 emits middleware frames
        // in whatever order reflection returns, the generated source is nondeterministic across
        // runs, which breaks byte-stable codegen caches and makes diagnostic diffs unreadable.
        // PR #2525 established this invariant for RPC-method discovery (GrpcServiceChain.cs:268);
        // we match that pattern here.
        var chain = await DiscoverStubChainAsync();

        var names = chain.DiscoveredBefores.Select(m => m.Name).ToArray();
        var sorted = names.OrderBy(n => n, StringComparer.Ordinal).ToArray();

        names.ShouldBe(sorted);
    }

    [Fact]
    public async Task discovered_afters_are_ordinally_sorted_for_deterministic_codegen()
    {
        var chain = await DiscoverStubChainAsync();

        var names = chain.DiscoveredAfters.Select(m => m.Name).ToArray();
        var sorted = names.OrderBy(n => n, StringComparer.Ordinal).ToArray();

        names.ShouldBe(sorted);
    }

    [Fact]
    public async Task discovery_results_are_stable_across_repeated_reads()
    {
        // Phase 1 codegen will read DiscoveredBefores from inside AssembleTypes which can be
        // invoked more than once during dynamic regeneration; cached results must not change
        // shape between calls or the generated source becomes nondeterministic.
        var chain = await DiscoverStubChainAsync();

        var first = chain.DiscoveredBefores;
        var second = chain.DiscoveredBefores;

        ReferenceEquals(first, second).ShouldBeTrue("DiscoveredBefores should be cached after first access");
    }

    private static async Task<GrpcServiceChain> DiscoverStubChainAsync()
    {
        DynamicCodeBuilder.WithinCodegenCommand = true;
        try
        {
            using var host = await Host.CreateDefaultBuilder()
                .UseWolverine(opts =>
                {
                    opts.ApplicationAssembly = typeof(GreeterMiddlewareTestStub).Assembly;
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

            return graph.Chains.Single(c => c.StubType == typeof(GreeterMiddlewareTestStub));
        }
        finally
        {
            DynamicCodeBuilder.WithinCodegenCommand = false;
        }
    }
}
