using JasperFx.CodeGeneration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Shouldly;
using Wolverine.Grpc.Tests.GrpcMiddlewareScoping.Generated;
using Xunit;

namespace Wolverine.Grpc.Tests.GrpcMiddlewareScoping;

/// <summary>
///     Verifies the post-discovery disambiguation pass in <see cref="GrpcGraph.DisambiguateCollidingTypeNames"/>.
///     Two proto services sharing a simple name (e.g. a <c>Greeter</c> in each of two bounded contexts)
///     would otherwise both emit <c>GreeterGrpcHandler</c> into the same <c>WolverineHandlers</c> child
///     namespace, and <c>AttachTypesSynchronously</c> would non-deterministically pick one.
/// </summary>
public class type_name_disambiguation_tests
{
    [Fact]
    public async Task colliding_chains_get_disambiguated_with_stable_hashed_suffix()
    {
        DynamicCodeBuilder.WithinCodegenCommand = true;
        try
        {
            using var host = await Host.CreateDefaultBuilder()
                .UseWolverine(opts => opts.ApplicationAssembly = typeof(GreeterMiddlewareTestStub).Assembly)
                .ConfigureServices(services => services.AddWolverineGrpc())
                .StartAsync();

            var graph = host.Services.GetRequiredService<GrpcGraph>();

            // Two stubs, different FullNames, same proto base → same default TypeName.
            var chainA = new GrpcServiceChain(typeof(AlphaCollisionStub), graph);
            var chainB = new GrpcServiceChain(typeof(BetaCollisionStub), graph);

            var originalName = chainA.TypeName;
            chainB.TypeName.ShouldBe(originalName,
                "pre-condition: both chains should emit the same default name so the disambiguator has something to fix");

            var chains = new List<GrpcServiceChain> { chainA, chainB };
            GrpcGraph.DisambiguateCollidingTypeNames(chains);

            chainA.TypeName.ShouldNotBe(chainB.TypeName);
            chainA.TypeName.ShouldStartWith(originalName + "_");
            chainB.TypeName.ShouldStartWith(originalName + "_");
        }
        finally
        {
            DynamicCodeBuilder.WithinCodegenCommand = false;
        }
    }

    [Fact]
    public async Task disambiguation_is_stable_and_idempotent()
    {
        DynamicCodeBuilder.WithinCodegenCommand = true;
        try
        {
            using var host = await Host.CreateDefaultBuilder()
                .UseWolverine(opts => opts.ApplicationAssembly = typeof(GreeterMiddlewareTestStub).Assembly)
                .ConfigureServices(services => services.AddWolverineGrpc())
                .StartAsync();

            var graph = host.Services.GetRequiredService<GrpcGraph>();

            var chainA = new GrpcServiceChain(typeof(AlphaCollisionStub), graph);
            var chainB = new GrpcServiceChain(typeof(BetaCollisionStub), graph);

            var chains = new List<GrpcServiceChain> { chainA, chainB };
            GrpcGraph.DisambiguateCollidingTypeNames(chains);

            var snapshotA = chainA.TypeName;
            var snapshotB = chainB.TypeName;

            // Re-running must be a no-op: names are already unique so no collision exists.
            GrpcGraph.DisambiguateCollidingTypeNames(chains);
            chainA.TypeName.ShouldBe(snapshotA);
            chainB.TypeName.ShouldBe(snapshotB);

            // A second pair of freshly-constructed chains from the same stub types must produce
            // the same disambiguated names — proves GetDeterministicHashCode is stable within
            // a process (which is the invariant Wolverine relies on for generated-code caching).
            var chainA2 = new GrpcServiceChain(typeof(AlphaCollisionStub), graph);
            var chainB2 = new GrpcServiceChain(typeof(BetaCollisionStub), graph);
            GrpcGraph.DisambiguateCollidingTypeNames([chainA2, chainB2]);
            chainA2.TypeName.ShouldBe(snapshotA);
            chainB2.TypeName.ShouldBe(snapshotB);
        }
        finally
        {
            DynamicCodeBuilder.WithinCodegenCommand = false;
        }
    }

    [Fact]
    public async Task non_colliding_chains_keep_their_default_type_names()
    {
        DynamicCodeBuilder.WithinCodegenCommand = true;
        try
        {
            using var host = await Host.CreateDefaultBuilder()
                .UseWolverine(opts => opts.ApplicationAssembly = typeof(GreeterMiddlewareTestStub).Assembly)
                .ConfigureServices(services => services.AddWolverineGrpc())
                .StartAsync();

            var graph = host.Services.GetRequiredService<GrpcGraph>();
            var chain = new GrpcServiceChain(typeof(AlphaCollisionStub), graph);
            var originalName = chain.TypeName;

            GrpcGraph.DisambiguateCollidingTypeNames([chain]);

            chain.TypeName.ShouldBe(originalName,
                "a single chain cannot collide with itself — its default name must be preserved");
        }
        finally
        {
            DynamicCodeBuilder.WithinCodegenCommand = false;
        }
    }

    // Internal stubs so reflection-based discovery (which walks GetExportedTypes) ignores them,
    // and the absence of [WolverineGrpcService] keeps them out of IsProtoFirstStub. Both safeguards
    // matter — otherwise these would land in scope_discovery_tests' chain lists and skew counts.
    internal abstract class AlphaCollisionStub : GreeterMiddlewareTest.GreeterMiddlewareTestBase;

    internal abstract class BetaCollisionStub : GreeterMiddlewareTest.GreeterMiddlewareTestBase;
}
