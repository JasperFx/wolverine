using JasperFx;
using JasperFx.CodeGeneration;
using Microsoft.Extensions.Hosting;
using Shouldly;
using Wolverine;
using Wolverine.Configuration;
using Wolverine.ErrorHandling;
using Wolverine.Runtime.Handlers;
using Wolverine.Tracking;
using Xunit;
using Xunit.Abstractions;

namespace CoreTests.Configuration;

// Characterization tests that PIN the order in which the various HandlerChain customization
// sources are applied during Compile/codegen. This is the contract the future source-generated
// handler manifest (GH-2906) must reproduce: the manifest will re-invoke policies + per-message
// configuration at startup, so the ordering — especially for error handling, where
// FailureRuleCollection is first-rule-wins — must stay identical.
//
// Sources, in the order established here:
//   1. IHandlerPolicy.Apply              (HandlerGraph.Compile, runs first)
//   2. ConfigureHandlerForMessage lambdas (HandlerGraph.Compile, after policies)
//   3. static Configure(HandlerChain)     (applyCustomizations, during codegen, last)
//
// Because chain.Failures matches the FIRST rule that fits, a rule contributed by an earlier
// source takes precedence over a later one for the same exception.
public class handler_chain_customization_ordering
{
    private readonly ITestOutputHelper _output;

    public handler_chain_customization_ordering(ITestOutputHelper output)
    {
        _output = output;
    }

    [Fact]
    public async Task policies_are_applied_before_explicit_chain_configuration()
    {
        using var host = await Host.CreateDefaultBuilder()
            .UseWolverine(opts =>
            {
                opts.Discovery.DisableConventionalDiscovery().IncludeType(typeof(OrderingMessageHandler));

                // (2) per-message explicit config
                opts.HandlerGraph.ConfigureHandlerForMessage<OrderingMessage>(chain =>
                {
                    chain.OnException<LambdaAddedException>().Discard();
                });

                // (1) handler policy
                opts.Policies.Add<ChainErrorPolicy>();
            }).StartAsync();

        var handlers = host.GetRuntime().Handlers;

        // ModifyChainAttribute / static Configure(HandlerChain) are applied during codegen
        // (HandlerChain.applyCustomizations), which is LAZY — it does not run at host StartAsync,
        // only when the chain is first compiled. Force that compile so the full, final ordering is
        // observable. This lazy timing is itself part of what GH-2906's eager generated manifest
        // must preserve as an equivalent final order.
        handlers.HandlerFor<OrderingMessage>().ShouldNotBeNull();

        var chain = handlers.ChainFor<OrderingMessage>();
        chain.ShouldNotBeNull();

        var rules = chain.Failures.ToList();
        foreach (var rule in rules)
        {
            _output.WriteLine(rule.ToString());
        }

        var policyIndex = rules.FindIndex(r => r.Match.Matches(new PolicyAddedException()));
        var lambdaIndex = rules.FindIndex(r => r.Match.Matches(new LambdaAddedException()));
        var configureIndex = rules.FindIndex(r => r.Match.Matches(new ConfigureAddedException()));

        policyIndex.ShouldBeGreaterThanOrEqualTo(0, "IHandlerPolicy error rule should be present");
        lambdaIndex.ShouldBeGreaterThanOrEqualTo(0, "ConfigureHandlerForMessage error rule should be present");
        configureIndex.ShouldBeGreaterThanOrEqualTo(0, "static Configure(HandlerChain) error rule should be present");

        // GH-2906 contract: policies first, then per-message lambdas, then the handler type's
        // static Configure(HandlerChain). First-rule-wins => earlier source has precedence.
        policyIndex.ShouldBeLessThan(lambdaIndex);
        lambdaIndex.ShouldBeLessThan(configureIndex);
    }
}

public record OrderingMessage;

public class PolicyAddedException : Exception;

public class LambdaAddedException : Exception;

public class ConfigureAddedException : Exception;

public static class OrderingMessageHandler
{
    // (3) Convention-discovered explicit configuration. Runs during codegen, after Compile.
    public static void Configure(HandlerChain chain)
    {
        chain.OnException<ConfigureAddedException>().Discard();
    }

    public static void Handle(OrderingMessage message)
    {
    }
}

// (1) A handler policy that contributes a chain-level error rule, the same way framework
// policies (e.g. AutoApplyTransactions, MartenAggregateHandlerStrategy) mutate chains.
public class ChainErrorPolicy : IHandlerPolicy
{
    public void Apply(IReadOnlyList<HandlerChain> chains, GenerationRules rules, IServiceContainer container)
    {
        foreach (var chain in chains)
        {
            chain.OnException<PolicyAddedException>().Discard();
        }
    }
}
