using Microsoft.Extensions.Hosting;
using Shouldly;
using Wolverine;
using Wolverine.Runtime;
using Wolverine.Runtime.Agents;
using Wolverine.Tracking;
using Wolverine.Transports.Tcp;
using Wolverine.Util;
using Xunit;

namespace CoreTests.Runtime.Routing;

public class explain_routing
{
    public record ExplainLocalMessage(Guid Id);
    public record ExplainPublishedMessage(Guid Id);
    public record ExplainUnroutedMessage(Guid Id);

    public static class ExplainLocalHandler
    {
        public static void Handle(ExplainLocalMessage message) { }
        public static void Handle(ExplainPublishedMessage message) { }
    }

    [Fact]
    public async Task explains_local_routing()
    {
        using var host = await Host.CreateDefaultBuilder()
            .UseWolverine(opts => opts.Discovery.IncludeType(typeof(ExplainLocalHandler)))
            .StartAsync();

        var explanation = host.GetRuntime().ExplainRoutingFor(typeof(ExplainLocalMessage));

        explanation.IsSystemMessageType.ShouldBeFalse();
        explanation.FinalRoutes.ShouldNotBeEmpty();

        var local = explanation.Steps.Single(x => x.Source.Name == "LocalRouting");
        local.SkipReason.ShouldBeNull();
        local.Produced.ShouldNotBeEmpty();
    }

    [Fact]
    public async Task explicit_routing_terminates_and_later_sources_are_skipped()
    {
        var port = PortFinder.GetAvailablePort();
        using var host = await Host.CreateDefaultBuilder()
            .UseWolverine(opts =>
            {
                opts.Discovery.IncludeType(typeof(ExplainLocalHandler));
                // Explicit publishing rule — ExplicitRouting is terminating
                opts.PublishMessage<ExplainPublishedMessage>().ToPort(port);
            })
            .StartAsync();

        var explanation = host.GetRuntime().ExplainRoutingFor(typeof(ExplainPublishedMessage));

        var explicitStep = explanation.Steps.Single(x => x.Source.Name == "ExplicitRouting");
        explicitStep.Source.IsAdditive.ShouldBeFalse();
        explicitStep.Produced.ShouldNotBeEmpty();

        // LocalRouting comes after the terminating ExplicitRouting and must be reported as skipped
        var localStep = explanation.Steps.Single(x => x.Source.Name == "LocalRouting");
        localStep.SkipReason.ShouldNotBeNull();
    }

    [Fact]
    public async Task explains_a_message_routed_nowhere()
    {
        using var host = await Host.CreateDefaultBuilder()
            .UseWolverine(opts => opts.Discovery.IncludeType(typeof(ExplainLocalHandler)))
            .StartAsync();

        var explanation = host.GetRuntime().ExplainRoutingFor(typeof(ExplainUnroutedMessage));

        explanation.FinalRoutes.ShouldBeEmpty();
        explanation.Steps.ShouldAllBe(x => x.Produced.Count == 0 || x.SkipReason != null);
    }

    [Fact]
    public async Task flags_system_message_types()
    {
        using var host = await Host.CreateDefaultBuilder().UseWolverine().StartAsync();

        var explanation = host.GetRuntime().ExplainRoutingFor(typeof(ExplainAgentCommand));
        explanation.IsSystemMessageType.ShouldBeTrue();
    }

    [Fact]
    public async Task text_output_carries_stable_labels_for_humans_and_agents()
    {
        using var host = await Host.CreateDefaultBuilder()
            .UseWolverine(opts => opts.Discovery.IncludeType(typeof(ExplainLocalHandler)))
            .StartAsync();

        var text = host.GetRuntime().ExplainRoutingFor(typeof(ExplainLocalMessage)).ToText();

        text.ShouldContain("MESSAGE:");
        text.ShouldContain("SYSTEM-MESSAGE-TYPE:");
        text.ShouldContain("ROUTE SOURCES");
        text.ShouldContain("SOURCE: LocalRouting");
        text.ShouldContain("FINAL ROUTES:");
    }
}

public record ExplainAgentCommand : IAgentCommand
{
    public Task<AgentCommands> ExecuteAsync(IWolverineRuntime runtime, CancellationToken cancellationToken)
        => Task.FromResult(AgentCommands.Empty);
}
