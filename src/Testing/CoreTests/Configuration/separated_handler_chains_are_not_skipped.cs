using JasperFx;
using JasperFx.CodeGeneration;
using JasperFx.Core.Reflection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Wolverine;
using Wolverine.Configuration;
using Wolverine.Diagnostics;
using Wolverine.ErrorHandling;
using Wolverine.Runtime;
using Wolverine.Runtime.Handlers;
using Wolverine.Tracking;
using Xunit;

namespace CoreTests.Configuration;

/// <summary>
/// GH-5133. Under MultipleHandlerBehavior.Separated, a message type with more than one handler gets an empty
/// placeholder root chain plus one chain per handler in <see cref="HandlerChain.ByEndpoint"/>. Anything that only
/// reads <see cref="HandlerGraph.Chains"/> sees the placeholder and misses every handler.
/// </summary>
public class separated_handler_chains_are_not_skipped : IAsyncLifetime
{
    private IHost _host = null!;

    public async ValueTask InitializeAsync()
    {
        _host = await Host.CreateDefaultBuilder()
            .UseWolverine(opts =>
            {
                opts.MultipleHandlerBehavior = MultipleHandlerBehavior.Separated;

                opts.Discovery.DisableConventionalDiscovery()
                    .IncludeType<InventoryModuleHandler>()
                    .IncludeType<ReportingModuleHandler>();

                // Requeue error handling on ONE of the two separated handlers only
                opts.Policies.Add(new RequeueForHandlerType(typeof(InventoryModuleHandler)));

                // Applies to every handler of the message type, separated or not
                opts.HandlerGraph.ConfigureHandlerForMessage<ModuleEvent>(chain =>
                {
                    chain.SuccessLogLevel = LogLevel.Information;
                });
            }).StartAsync();
    }

    public async ValueTask DisposeAsync()
    {
        await _host.StopAsync();
        _host.Dispose();
    }

    private HandlerGraph theGraph => _host.GetRuntime().Handlers;

    [Fact]
    public void the_root_chain_is_an_empty_placeholder()
    {
        // The precondition for every test below
        var root = theGraph.ChainFor<ModuleEvent>()!;
        root.Handlers.ShouldBeEmpty();
        root.ByEndpoint.Count.ShouldBe(2);
    }

    [Fact]
    public void every_chain_includes_the_separated_chains()
    {
        theGraph.EveryChain()
            .Where(x => x.MessageType == typeof(ModuleEvent))
            .SelectMany(x => x.Handlers)
            .Select(x => x.HandlerType)
            .ShouldBe([typeof(InventoryModuleHandler), typeof(ReportingModuleHandler)], ignoreOrder: true);
    }

    [Fact]
    public void requeue_policy_on_a_separated_chain_is_seen_by_listener_validation()
    {
        // Was false: the requeue validation only read the root chains
        theGraph.Chains.Any(x => x.Failures.AnyRequeuePolicies()).ShouldBeFalse();

        theGraph.AnyRequeuePolicies().ShouldBeTrue();
    }

    [Fact]
    public void ConfigureHandlerForMessage_reaches_every_separated_chain()
    {
        var root = theGraph.ChainFor<ModuleEvent>()!;
        root.ByEndpoint.ShouldAllBe(x => x.SuccessLogLevel == LogLevel.Information);
    }

    [Fact]
    public void describe_routing_lists_the_separated_handlers()
    {
        var lines = WolverineDiagnosticsCommand.DescribeLocalHandlers(theGraph.ChainFor<ModuleEvent>()!);

        lines.Count.ShouldBe(2);
        lines.ShouldContain(x => x.StartsWith(typeof(InventoryModuleHandler).FullNameInCode() + ".Handle") && x.Contains("local://"));
        lines.ShouldContain(x => x.StartsWith(typeof(ReportingModuleHandler).FullNameInCode() + ".Handle") && x.Contains("local://"));
    }

    [Fact]
    public void error_handling_description_names_the_separated_handler()
    {
        var chain = theGraph.EveryChain().Single(x => x.Failures.Any());

        var title = WolverineSystemPart.ErrorHandlingTitleFor(chain);
        title.ShouldContain(typeof(ModuleEvent).FullNameInCode());
        title.ShouldContain(typeof(InventoryModuleHandler).FullNameInCode());
    }
}

public record ModuleEvent(Guid Id);

// Hidden from conventional discovery so it cannot leak into every other host in this assembly
[Wolverine.Attributes.WolverineIgnore]
public class InventoryModuleHandler
{
    public void Handle(ModuleEvent message)
    {
    }
}

// Hidden from conventional discovery so it cannot leak into every other host in this assembly
[Wolverine.Attributes.WolverineIgnore]
public class ReportingModuleHandler
{
    public void Handle(ModuleEvent message)
    {
    }
}

public class RequeueForHandlerType : IHandlerPolicy
{
    private readonly Type _handlerType;

    public RequeueForHandlerType(Type handlerType)
    {
        _handlerType = handlerType;
    }

    public void Apply(IReadOnlyList<HandlerChain> chains, GenerationRules rules, IServiceContainer container)
    {
        foreach (var chain in chains.Where(x => x.Handlers.Any(h => h.HandlerType == _handlerType)))
        {
            chain.OnException<TimeoutException>().Requeue();
        }
    }
}

/// <summary>
/// GH-5133. Two saga types that handle the same messages: under Separated each gets its own chain in
/// ByEndpoint and the root chain of each message type is routing-only.
/// </summary>
public class separated_saga_chains_are_not_skipped : IAsyncLifetime
{
    private IHost _host = null!;

    public async ValueTask InitializeAsync()
    {
        _host = await Host.CreateDefaultBuilder()
            .UseWolverine(opts =>
            {
                opts.MultipleHandlerBehavior = MultipleHandlerBehavior.Separated;

                opts.Discovery.DisableConventionalDiscovery()
                    .IncludeType<FirstSharedWorkSaga>()
                    .IncludeType<SecondSharedWorkSaga>();

                opts.MessagePartitioning.UseInferredMessageGrouping();
            }).StartAsync();
    }

    public async ValueTask DisposeAsync()
    {
        await _host.StopAsync();
        _host.Dispose();
    }

    private WolverineRuntime theRuntime => _host.GetRuntime();

    [Fact]
    public void inferred_grouping_finds_the_saga_identity_in_a_separated_chain()
    {
        var id = Guid.NewGuid();

        theRuntime.Options.MessagePartitioning
            .DetermineGroupId(new Envelope(new SharedWorkStep(id)))
            .ShouldBe(id.ToString());
    }
}

public record StartSharedWork(Guid Id);

public record SharedWorkStep(Guid Id);

// Hidden from conventional discovery so it cannot leak into every other host in this assembly
[Wolverine.Attributes.WolverineIgnore]
public class FirstSharedWorkSaga : Saga
{
    public Guid Id { get; set; }

    public static FirstSharedWorkSaga Start(StartSharedWork message) => new() { Id = message.Id };

    public void Handle(SharedWorkStep message)
    {
    }
}

// Hidden from conventional discovery so it cannot leak into every other host in this assembly
[Wolverine.Attributes.WolverineIgnore]
public class SecondSharedWorkSaga : Saga
{
    public Guid Id { get; set; }

    public static SecondSharedWorkSaga Start(StartSharedWork message) => new() { Id = message.Id };

    public void Handle(SharedWorkStep message)
    {
    }
}
