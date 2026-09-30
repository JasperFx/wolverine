using JasperFx.CodeGeneration.Frames;
using Microsoft.Extensions.Hosting;
using Shouldly;
using Wolverine;
using Wolverine.Attributes;
using Wolverine.Runtime;
using Wolverine.Runtime.Handlers;
using Wolverine.Tracking;
using Xunit;

namespace CoreTests.Configuration;

// GH-4716. [Transactional] resolves its owner through SelectTransactionOwner, which falls back to
// InMemoryPersistenceFrameProvider -- whose ApplyTransactionSupport is empty and whose CanApply is
// hardwired false -- when no provider claims the chain. So on a host with no persistence the attribute
// does nothing at all, silently, and then set chain.IsTransactional = true anyway.
//
// These characterize what that wrong flag actually cost, which is the thing the issue asked to establish.
// The answer is narrower than it looks, and the reason is an ordering fact worth having written down:
// [Transactional] is a ModifyChainAttribute, applied from HandlerChain.applyCustomizations at CODEGEN time,
// which is strictly after every IHandlerPolicy has run. So a policy reading IsTransactional never sees what
// this attribute wrote, whether the attribute worked or not.

public class transactional_with_no_provider_4716
{
    public record NoProviderMessage;

    [Transactional]
    public class NoProviderMessageHandler
    {
        public void Handle(NoProviderMessage message)
        {
        }
    }

    private static async Task<HandlerChain> chainFor(Action<WolverineOptions>? configure = null)
    {
        using var host = await Host.CreateDefaultBuilder()
            .UseWolverine(opts =>
            {
                opts.ApplicationAssembly = typeof(transactional_with_no_provider_4716).Assembly;
                opts.Discovery.IncludeType<NoProviderMessageHandler>();
                configure?.Invoke(opts);
            }).StartAsync();

        // Attributes are applied from applyCustomizations, which only runs when the chain's source is
        // actually generated -- reading ChainFor alone sees a chain [Transactional] has not touched yet.
        host.GetRuntime().Handlers.HandlerFor<NoProviderMessage>();

        return host.GetRuntime().Handlers.ChainFor<NoProviderMessage>()!;
    }

    [Fact]
    public async Task no_transaction_is_actually_applied()
    {
        var chain = await chainFor();

        // The baseline fact: nothing was woven in. InMemoryPersistenceFrameProvider.ApplyTransactionSupport
        // is an empty method, so the "transaction" is entirely notional.
        chain.Middleware.OfType<MethodCall>()
            .Any(x => x.Method.Name.Contains("Transaction", StringComparison.OrdinalIgnoreCase))
            .ShouldBeFalse();
    }

    [Fact]
    public async Task the_chain_does_not_claim_to_be_transactional()
    {
        var chain = await chainFor();

        // THE GH-4716 defect: this used to be true after applying no transaction at all. Consumers of
        // IChain are entitled to read the flag as "this chain commits a unit of work", and this one does not.
        chain.IsTransactional.ShouldBeFalse();
    }

    [Fact]
    public async Task eager_idempotency_still_guards_the_chain()
    {
        var chain = await chainFor(opts => opts.Policies.AutoApplyIdempotencyOnNonTransactionalHandlers());

        // The opt-in exists to cover chains with no transaction to lean on, and this is exactly such a
        // chain, so the guard has to be here. It survived the defect only by the accident of ordering
        // described above -- EagerIdempotencyOnNonTransactionalChains is an IHandlerPolicy and had already
        // run by the time the attribute set the flag. With IsTransactional no longer lying, it is right for
        // the stated reason rather than the accidental one.
        chain.Middleware.OfType<MethodCall>()
            .ShouldContain(x => x.Method.Name == nameof(MessageContext.AssertEagerIdempotencyAsync));

        chain.Postprocessors.OfType<MethodCall>()
            .ShouldContain(x => x.Method.Name == nameof(MessageContext.PersistHandledAsync));
    }
}
