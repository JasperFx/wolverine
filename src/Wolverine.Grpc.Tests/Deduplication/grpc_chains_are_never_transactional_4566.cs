using JasperFx;
using JasperFx.CodeGeneration;
using JasperFx.CodeGeneration.Frames;
using JasperFx.CodeGeneration.Model;
using Microsoft.Extensions.DependencyInjection;
using Shouldly;
using Wolverine.Configuration;
using Wolverine.Persistence;
using Wolverine.Persistence.Sagas;
using Wolverine.Runtime;
using Xunit;

namespace Wolverine.Grpc.Tests.Deduplication;

/// <summary>
/// GH-4566 asked why gRPC chains bypass <c>ApplyDeduplication</c> and therefore never get GH-4505's
/// transactional deduplication claim, and framed it as a missed optimisation: two round trips on the
/// happy path where a Marten-backed handler needs one.
///
/// <para>
/// It is not an optimisation, because <b>a gRPC chain can never carry a store transaction at all</b>, and
/// that is structural rather than an oversight. These tests pin the two halves of why, so that the
/// divergence cannot quietly turn into a real gap later.
/// </para>
///
/// <para>
/// <b>Half one — the policy really does reach gRPC chains.</b> It would be comfortable to assume the
/// answer is "transaction policies skip gRPC", and that assumption is wrong:
/// <c>GrpcGraph.Discover</c> runs every registered <see cref="IChainPolicy" /> over the gRPC chains,
/// <c>AutoApplyTransactions</c> included.
/// </para>
///
/// <para>
/// <b>Half two — but the chain offers a provider nothing to match on.</b> All three gRPC chain types
/// return an empty <c>HandlerCalls()</c>, so an RPC method's own parameters never surface as service
/// dependencies. A persistence provider's <c>CanApply</c> asks exactly that question, so it always
/// declines, no transaction support is ever applied, and
/// <c>IPersistenceFrameProvider.TryBuildTransactionalDeduplication</c> — which requires the session and
/// commit frames to be present — would return false on every gRPC chain even if it were wired up.
/// </para>
///
/// <para>
/// So the compensating release is not a slower path that Marten chains have outgrown; on gRPC it is the
/// only correct one. If someone ever makes a gRPC chain expose its RPC methods as handler calls — which
/// is the change that would make GH-4566 real — the second test here fails and says so.
/// </para>
/// </summary>
public class grpc_chains_are_never_transactional_4566
{
    [Fact]
    public async Task chain_policies_really_do_reach_grpc_chains()
    {
        RecordingChainPolicy.Seen.Clear();

        await using var host = await DeduplicationGrpcHost.StartAsync(opts =>
        {
            opts.Policies.Add<RecordingChainPolicy>();

            // Registered into the same list, and therefore applied to the same chains, as the recorder.
            opts.Policies.AutoApplyTransactions();
        });

        // Force the chains to be built, which is what runs the policies.
        var graph = host.Services.GetRequiredService<GrpcGraph>();
        var chain = graph.CodeFirstChains.Single(c => c.ServiceContractType == typeof(IDeduplicatedEchoService));

        // The surprising half: an IChainPolicy IS offered the gRPC chains, so "transaction policies skip
        // gRPC" is not the explanation. AutoApplyTransactions went through this same list.
        RecordingChainPolicy.Seen.ShouldContain(x => ReferenceEquals(x, chain));

        // ...and it still did not make the chain transactional, because of the next test.
        chain.IsTransactional.ShouldBeFalse();
    }

    [Fact]
    public async Task a_grpc_chain_offers_a_persistence_provider_nothing_to_match_on()
    {
        await using var host = await DeduplicationGrpcHost.StartAsync();

        var graph = host.Services.GetRequiredService<GrpcGraph>();
        var chain = graph.CodeFirstChains.Single(c => c.ServiceContractType == typeof(IDeduplicatedEchoService));

        // The root cause, stated directly. An RPC method's parameters are never walked for service
        // dependencies because there are no handler calls to walk -- Chain.serviceDependencies iterates
        // Middleware.OfType<MethodCall>() and HandlerCalls(), and this is empty.
        chain.HandlerCalls().ShouldBeEmpty();

        var container = host.Services.GetRequiredService<IServiceContainer>();
        chain.ServiceDependencies(container, []).ShouldBeEmpty();

        // ...so nothing ever marks the chain transactional, on any code path.
        chain.IsTransactional.ShouldBeFalse();
    }

    [Fact]
    public async Task every_deduplicated_rpc_keeps_its_compensating_release()
    {
        await using var host = await DeduplicationGrpcHost.StartAsync();

        var graph = host.Services.GetRequiredService<GrpcGraph>();
        var chain = graph.CodeFirstChains.Single(c => c.ServiceContractType == typeof(IDeduplicatedEchoService));

        var source = chain.SourceCode;
        source.ShouldNotBeNull();

        // The consequence of the two tests above, and the thing a reader of GH-4566 actually cares about:
        // the claim is committed before the forward runs, so the release is what makes a failed forward
        // give the id back. Two of the three RPCs are deduplicated.
        (source!.Split("ReleaseAsync").Length - 1).ShouldBe(2);

        // And there is no commit for a claim to ride. A transactional chain's generated method ends in a
        // SaveChangesAsync; this one has nothing of the sort, which is the same fact the two tests above
        // establish from the other end.
        source.ShouldNotContain("SaveChangesAsync");
    }

    /// <summary>
    /// Records every chain it is handed and changes nothing, so the test observes what the policy pass
    /// sees without altering what any other policy does.
    /// </summary>
    internal class RecordingChainPolicy : IChainPolicy
    {
        public static readonly List<IChain> Seen = [];

        public void Apply(IReadOnlyList<IChain> chains, GenerationRules rules, IServiceContainer container)
        {
            lock (Seen)
            {
                Seen.AddRange(chains);
            }
        }
    }
}
