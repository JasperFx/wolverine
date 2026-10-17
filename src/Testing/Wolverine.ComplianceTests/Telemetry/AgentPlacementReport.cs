using Bobcat;
using Bobcat.Engine;
using JasperFx.Core;
using Microsoft.Extensions.Hosting;
using Wolverine.Tracking;

namespace Wolverine.ComplianceTests.Telemetry;

/// <summary>
///     Where every agent was at the moment a wait gave up: which node its assignment row names against which nodes
///     are actually running it. Replaces the "According to the database… / According to the runtimes…" text
///     <c>AssignmentWaiter</c> puts in its <c>TimeoutException</c>, as one grid where the agents the two disagree
///     about are the red rows.
/// </summary>
/// <remarks>
///     Built on the test's own side, inside the step that timed out, so this one is an ordinary
///     <see cref="TableReport" /> -- the simple case, next to <see cref="AgentAssignmentTimeline" />'s background one.
///     A red row explains a failure but does not cause one: the step's checks are the claim.
/// </remarks>
public sealed class AgentPlacementReport : TableReport
{
    public override string Title => "Agent placement: assignment rows against running agents";

    public override string? ShortTitle => "Placement";

    /// <summary>
    ///     Replace this scenario's placement report with the cluster as it is now. A no-op outside a specification.
    ///     Replaces rather than appends, so the report shows the LAST wait that gave up.
    /// </summary>
    /// <param name="hosts">The nodes the specification is running.</param>
    /// <param name="paused">Agents the specification paused, which are healthy when they run nowhere.</param>
    public static async Task CaptureAsync(IReadOnlyList<IHost> hosts, IReadOnlyCollection<Uri>? paused = null)
    {
        if (!SpecReport.IsRecording || hosts.Count == 0) return;

        paused ??= [];

        var persisted = await hosts[0].GetRuntime().Storage.Nodes.LoadAllNodesAsync(CancellationToken.None);
        var tracked = hosts.Select(x => x.GetRuntime().Options.UniqueNodeId).ToHashSet();

        var rows = persisted
            .SelectMany(n => n.ActiveAgents.Select(a => (Agent: a, Node: n)))
            .GroupBy(x => x.Agent)
            .ToDictionary(g => g.Key, g => g.Select(x => x.Node).OrderBy(x => x.AssignedNodeNumber).ToArray());

        var running = hosts
            .Select(h => (Number: h.GetRuntime().Options.Durability.AssignedNodeNumber, Agents: h.RunningAgents()))
            .ToArray();

        var agents = rows.Keys.Concat(running.SelectMany(x => x.Agents))
            .Concat(FakeAgentFamily.Names.Select(x => new Uri($"fake://{x}")))
            .Where(x => !x.Scheme.StartsWith("wolverine"))
            .Distinct()
            .OrderBy(x => x.ToString())
            .ToArray();

        var report = new AgentPlacementReport();
        foreach (var agent in agents)
        {
            var named = rows.TryGetValue(agent, out var owners) ? owners : [];
            var runners = running.Where(x => x.Agents.Contains(agent)).Select(x => x.Number).ToArray();

            var rowText = named.Length == 0
                ? "no row"
                : named.Select(n => tracked.Contains(n.NodeId)
                    ? n.AssignedNodeNumber.ToString()
                    : $"{n.AssignedNodeNumber} (not a running node)").Join(", ");
            var runningText = runners.Length == 0 ? "nowhere" : runners.Select(x => x.ToString()).Join(", ");

            var isPaused = paused.Contains(agent);
            var healthy = isPaused
                ? runners.Length == 0
                : runners.Length == 1 && named.Length == 1 && named[0].AssignedNodeNumber == runners[0];

            CellResult verdict;
            if (healthy)
            {
                verdict = new CellResult("verdict", ResultStatus.success,
                    isPaused ? "paused, running nowhere" : "one copy, row agrees");
            }
            else
            {
                verdict = new CellResult("verdict", ResultStatus.failed)
                {
                    Expected = isPaused ? "running nowhere"
                        : named.Length == 1 ? $"running once, on node {named[0].AssignedNodeNumber}"
                        : "running once, with one row naming its node",
                    Actual = runners.Length switch
                    {
                        0 => "running nowhere",
                        1 => $"running on node {runners[0]}",
                        _ => $"running {runners.Length} times, on nodes {runningText}"
                    }
                };
            }

            report.Row([
                new CellResult("agent", ResultStatus.ok, agent.ToString()),
                new CellResult("row names", ResultStatus.ok, rowText),
                new CellResult("running on", ResultStatus.ok, runningText),
                verdict
            ]);
        }

        SpecReport.Attach(report);
    }
}
