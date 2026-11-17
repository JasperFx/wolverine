using Bobcat;
using Bobcat.Xunit;
using Microsoft.Extensions.Hosting;
using Wolverine;
using Xunit;

namespace CoreTests.Runtime.Agents.Workloads;

/// <summary>
///     GH-5601. A workload setting a node could not honor stops it at startup, before the message store initializes
///     or any transport connects, with a message that says what to change. Decision D5: Durability without Messaging
///     is refused, because a node holding the durability agents recovers inbox envelopes through its own listeners.
/// </summary>
[BobcatFeature("A node's workloads are checked when it starts")]
public class node_workloads_are_checked_at_startup_5601
{
    private readonly NodeStartup the = new();

    [BobcatSpec]
    public async Task durability_without_messaging_is_refused()
    {
        await the.ANodeStarts(NodeWorkloads.Durability);

        the.ItRefusesToStartSaying("has Durability without Messaging");
    }

    [BobcatSpec]
    public async Task durability_with_messaging_starts()
    {
        await the.ANodeStarts(NodeWorkloads.Messaging | NodeWorkloads.Durability);

        the.ItStarts();
    }

    [BobcatSpec]
    public async Task an_http_only_node_starts()
    {
        await the.ANodeStarts(NodeWorkloads.None);

        the.ItStarts();
    }

    [BobcatSpec]
    public async Task a_solo_node_has_to_run_everything()
    {
        await the.ANodeStartsInMode(DurabilityMode.Solo, NodeWorkloads.Messaging);

        the.ItRefusesToStartSaying("Solo mode, where the one node has to do everything");
    }

    [BobcatSpec]
    public async Task a_warmup_node_needs_projections()
    {
        await the.AWarmupNodeStarts(NodeWorkloads.Messaging);

        the.ItRefusesToStartSaying("A warm-up node only warms up projections");
    }

    [BobcatSpec]
    public async Task the_mediator_only_mode_ignores_workloads()
    {
        await the.ANodeStartsInMode(DurabilityMode.MediatorOnly, NodeWorkloads.Durability);

        the.ItStarts();
    }
}

/// <summary>The vocabulary of <see cref="node_workloads_are_checked_at_startup_5601" />: one node, no message store.</summary>
public sealed class NodeStartup
{
    private Exception? _failure;

    [When("a node starts with workloads {workloads}")]
    public Task ANodeStarts(NodeWorkloads workloads) => startAsync(DurabilityMode.Balanced, workloads, NodeRole.Serving);

    [When("a {mode} node starts with workloads {workloads}")]
    public Task ANodeStartsInMode(DurabilityMode mode, NodeWorkloads workloads) =>
        startAsync(mode, workloads, NodeRole.Serving);

    [When("a warm-up node starts with workloads {workloads}")]
    public Task AWarmupNodeStarts(NodeWorkloads workloads) => startAsync(DurabilityMode.Balanced, workloads, NodeRole.Warmup);

    [Then("it refuses to start: \"{text}\"")]
    public void ItRefusesToStartSaying(string text)
    {
        SpecAssert.Check("started", _failure == null, false);
        if (_failure != null)
        {
            SpecAssert.Check("refused because", _failure.Message.Contains(text) ? text : _failure.Message, text);
        }
    }

    [Then("it starts")]
    public void ItStarts()
    {
        SpecAssert.Check("refused because", _failure?.Message ?? "it was not refused", "it was not refused");
    }

    private async Task startAsync(DurabilityMode mode, NodeWorkloads workloads, NodeRole role)
    {
        try
        {
            using var host = await Host.CreateDefaultBuilder()
                .UseWolverine(opts =>
                {
                    opts.ApplicationAssembly = GetType().Assembly;
                    opts.Durability.Mode = mode;
                    opts.Durability.Workloads = workloads;
                    opts.Durability.NodeRole = role;
                }).StartAsync(TestContext.Current.CancellationToken);

            await host.StopAsync(TestContext.Current.CancellationToken);
        }
        catch (Exception e)
        {
            _failure = e;
            SpecLog.Write($"{e.GetType().Name}: {e.Message}");
        }
    }
}
