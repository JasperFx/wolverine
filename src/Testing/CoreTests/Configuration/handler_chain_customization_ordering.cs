using JasperFx;
using JasperFx.CodeGeneration;
using Microsoft.Extensions.Hosting;
using Shouldly;
using Wolverine;
using Wolverine.Attributes;
using Wolverine.Configuration;
using Wolverine.ErrorHandling;
using Wolverine.Persistence.Sagas;
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
//   1. IHandlerPolicy.Apply               (HandlerGraph.Compile, eager, runs first)
//   2. ConfigureHandlerForMessage lambdas (HandlerGraph.Compile, eager, after policies)
//   3. ModifyHandlerChainAttribute        (applyCustomizations during codegen, HandlerChain.cs:694)
//   4. static Configure(HandlerChain)     (applyCustomizations during codegen, HandlerChain.cs:705, last)
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

        AssertCustomizationOrder(rules);
    }

    [Fact]
    public async Task ordering_is_preserved_for_saga_chains()
    {
        using var host = await Host.CreateDefaultBuilder()
            .UseWolverine(opts =>
            {
                opts.Discovery.DisableConventionalDiscovery().IncludeType(typeof(OrderingSaga));

                opts.HandlerGraph.ConfigureHandlerForMessage<StartOrderingSaga>(chain =>
                {
                    chain.OnException<LambdaAddedException>().Discard();
                });

                opts.Policies.Add<ChainErrorPolicy>();
            }).StartAsync();

        var handlers = host.GetRuntime().Handlers;
        handlers.HandlerFor<StartOrderingSaga>().ShouldNotBeNull();

        var chain = handlers.ChainFor<StartOrderingSaga>();
        chain.ShouldNotBeNull();

        // Confirm we really exercised the saga emit path, not a plain HandlerChain.
        chain.ShouldBeOfType<SagaChain>();

        var rules = chain.Failures.ToList();
        foreach (var rule in rules)
        {
            _output.WriteLine(rule.ToString());
        }

        // SagaChain.DetermineFrames calls applyCustomizations FIRST (SagaChain.cs:245), before
        // building its saga-specific frames, so the customization ordering is identical to a plain
        // HandlerChain. GH-2906, Q3: the generated saga emit path must run this same sequence.
        AssertCustomizationOrder(rules);
    }

    [Fact]
    public async Task ordering_is_preserved_for_sticky_endpoint_chains()
    {
        using var host = await Host.CreateDefaultBuilder()
            .UseWolverine(opts =>
            {
                // Two sticky handlers so the chain splits into per-endpoint child chains
                // (the split only happens when more than one handler targets the message type).
                opts.Discovery.DisableConventionalDiscovery()
                    .IncludeType(typeof(OrangeStickyHandler))
                    .IncludeType(typeof(PurpleStickyHandler));

                opts.Policies.Add<ChainErrorPolicy>();
            }).StartAsync();

        var handlers = host.GetRuntime().Handlers;

        var parent = handlers.ChainFor<StickyOrderingMessage>();
        parent.ShouldNotBeNull();

        // Both sticky handlers are split out into their own per-endpoint child chains; the parent
        // keeps no default handlers.
        parent.ByEndpoint.Count.ShouldBe(2);
        parent.Handlers.ShouldBeEmpty();

        var sticky = parent.ByEndpoint.Single(c => c.Handlers.Single().HandlerType == typeof(OrangeStickyHandler));

        // Q4: the child chain's endpoint is resolved (to a local queue named after the attribute)
        // by the time the chain is built — the contract behind "emit Uris, resolve post-transport-config".
        var endpoint = sticky.Endpoints.ShouldHaveSingleItem();
        endpoint.EndpointName.ShouldBe("orange");

        // Force the child chain to compile so its applyCustomizations (attribute + Configure) runs.
        handlers.HandlerFor(typeof(StickyOrderingMessage), endpoint).ShouldNotBeNull();

        var rules = sticky.Failures.ToList();
        foreach (var rule in rules)
        {
            _output.WriteLine(rule.ToString());
        }

        var policyIndex = rules.FindIndex(r => r.Match.Matches(new PolicyAddedException()));
        var attributeIndex = rules.FindIndex(r => r.Match.Matches(new AttributeAddedException()));
        var configureIndex = rules.FindIndex(r => r.Match.Matches(new ConfigureAddedException()));

        policyIndex.ShouldBeGreaterThanOrEqualTo(0, "IHandlerPolicy error rule should reach the sticky child chain");
        attributeIndex.ShouldBeGreaterThanOrEqualTo(0, "ModifyHandlerChainAttribute error rule should reach the sticky child chain");
        configureIndex.ShouldBeGreaterThanOrEqualTo(0, "static Configure(HandlerChain) error rule should reach the sticky child chain");

        // GH-2906, Q4: generated code must apply customizations to each per-endpoint child chain,
        // preserving the same relative order: policy -> attribute -> static Configure.
        policyIndex.ShouldBeLessThan(attributeIndex);
        attributeIndex.ShouldBeLessThan(configureIndex);
    }

    // GH-2906 contract (first-rule-wins => earlier source has precedence):
    //   policy -> ConfigureHandlerForMessage lambda -> ModifyHandlerChainAttribute -> static Configure.
    // The attribute and Configure both run inside applyCustomizations, but the attribute pass
    // (HandlerChain.cs:694) precedes the static Configure pass (HandlerChain.cs:705).
    private static void AssertCustomizationOrder(List<FailureRule> rules)
    {
        var policyIndex = rules.FindIndex(r => r.Match.Matches(new PolicyAddedException()));
        var lambdaIndex = rules.FindIndex(r => r.Match.Matches(new LambdaAddedException()));
        var attributeIndex = rules.FindIndex(r => r.Match.Matches(new AttributeAddedException()));
        var configureIndex = rules.FindIndex(r => r.Match.Matches(new ConfigureAddedException()));

        policyIndex.ShouldBeGreaterThanOrEqualTo(0, "IHandlerPolicy error rule should be present");
        lambdaIndex.ShouldBeGreaterThanOrEqualTo(0, "ConfigureHandlerForMessage error rule should be present");
        attributeIndex.ShouldBeGreaterThanOrEqualTo(0, "ModifyHandlerChainAttribute error rule should be present");
        configureIndex.ShouldBeGreaterThanOrEqualTo(0, "static Configure(HandlerChain) error rule should be present");

        policyIndex.ShouldBeLessThan(lambdaIndex);
        lambdaIndex.ShouldBeLessThan(attributeIndex);
        attributeIndex.ShouldBeLessThan(configureIndex);
    }
}

[AddAttributeErrorRule]
public record OrderingMessage;

public class PolicyAddedException : Exception;

public class LambdaAddedException : Exception;

public class AttributeAddedException : Exception;

public class ConfigureAddedException : Exception;

// (3) A ModifyHandlerChainAttribute on the message type. Applied inside applyCustomizations
// (HandlerChain.cs:694), before the static Configure(HandlerChain) pass (HandlerChain.cs:705).
public class AddAttributeErrorRuleAttribute : ModifyHandlerChainAttribute
{
    public override void Modify(HandlerChain chain, GenerationRules rules)
    {
        chain.OnException<AttributeAddedException>().Discard();
    }
}

public static class OrderingMessageHandler
{
    // (4) Convention-discovered explicit configuration. Runs during codegen, last.
    public static void Configure(HandlerChain chain)
    {
        chain.OnException<ConfigureAddedException>().Discard();
    }

    public static void Handle(OrderingMessage message)
    {
    }
}

// Saga variant — the same four sources applied to a Saga, whose chain is a SagaChain with its
// own DetermineFrames emit path (which still calls applyCustomizations first; SagaChain.cs:245).
[AddAttributeErrorRule]
public record StartOrderingSaga(Guid Id);

public class OrderingSaga : Saga
{
    public Guid Id { get; set; }

    public static OrderingSaga Start(StartOrderingSaga command)
    {
        return new OrderingSaga { Id = command.Id };
    }

    // (4) static Configure(HandlerChain) on the saga type.
    public static void Configure(HandlerChain chain)
    {
        chain.OnException<ConfigureAddedException>().Discard();
    }
}

// Sticky variant — the handler is bound to a named endpoint, so its chain is split into a
// per-endpoint child chain (HandlerChain.ByEndpoint) that must receive the same customizations.
[AddAttributeErrorRule]
public record StickyOrderingMessage;

[StickyHandler("orange")]
public static class OrangeStickyHandler
{
    // (4) static Configure(HandlerChain) on the sticky handler type.
    public static void Configure(HandlerChain chain)
    {
        chain.OnException<ConfigureAddedException>().Discard();
    }

    public static void Handle(StickyOrderingMessage message)
    {
    }
}

// Second sticky handler — present only so the chain splits into per-endpoint child chains.
[StickyHandler("purple")]
public static class PurpleStickyHandler
{
    public static void Handle(StickyOrderingMessage message)
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
