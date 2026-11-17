using Bobcat;
using Bobcat.Xunit;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Wolverine.Runtime;
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

    /// <summary>
    ///     A test fixture that runs an application configured as an HTTP-only node in Solo mode still works: the one
    ///     node of a Solo "cluster" has to do everything, so the setting is ignored, out loud.
    /// </summary>
    [BobcatSpec]
    public async Task a_solo_node_ignores_its_workloads_and_runs_everything()
    {
        await the.ANodeStartsInMode(DurabilityMode.Solo, NodeWorkloads.None);

        the.ItStarts();
        the.ItRunsWorkloads(NodeWorkloads.All);
        the.ItWarns("runs in Solo mode, where the one node has to do everything, so it is ignored");
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
    private NodeWorkloads? _workloads;
    private readonly List<string> _warnings = [];

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

    [Then("it runs workloads {workloads}")]
    public void ItRunsWorkloads(NodeWorkloads workloads)
    {
        SpecAssert.Check("workloads", _workloads?.ToString() ?? "it never started", workloads.ToString());
    }

    [Then("it warns \"{text}\"")]
    public void ItWarns(string text)
    {
        var found = _warnings.Any(x => x.Contains(text));
        SpecAssert.Check("a warning saying", found ? text : $"none, among: {(_warnings.Count == 0 ? "no warnings" : string.Join(" | ", _warnings))}", text);
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
                    opts.Services.AddSingleton<ILoggerProvider>(new WarningRecorder(_warnings));
                }).StartAsync(TestContext.Current.CancellationToken);

            _workloads = host.Services.GetRequiredService<IWolverineRuntime>().Options.Durability.Workloads;

            await host.StopAsync(TestContext.Current.CancellationToken);
        }
        catch (Exception e)
        {
            _failure = e;
            SpecLog.Write($"{e.GetType().Name}: {e.Message}");
        }
    }
}

internal sealed class WarningRecorder(List<string> warnings) : ILoggerProvider, ILogger
{
    public ILogger CreateLogger(string categoryName) => this;
    public void Dispose() { }
    public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
    public bool IsEnabled(LogLevel logLevel) => logLevel >= LogLevel.Warning;

    public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception,
        Func<TState, Exception?, string> formatter)
    {
        if (logLevel < LogLevel.Warning) return;
        lock (warnings) warnings.Add(formatter(state, exception));
    }
}
