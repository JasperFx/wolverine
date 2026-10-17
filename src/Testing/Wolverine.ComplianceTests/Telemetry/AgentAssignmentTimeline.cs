using Bobcat.Engine;

namespace Wolverine.ComplianceTests.Telemetry;

/// <summary>
///     The cluster's leadership and assignment history as a Bobcat report: one row per node event, per command a
///     leader issued, per agent a node started or stopped, and per thing the specification did -- with the runs of
///     evaluations that changed nothing folded into one row each. Written out only for a failed specification
///     (<see cref="ReportVisibility.OnFailure" />), capped at <see cref="TableReport.DefaultMaxRows" /> rows with
///     "…and N more".
/// </summary>
/// <remarks>
///     <b>An <see cref="IScenarioReport" /> rather than a <see cref="TableReport" /> subclass</b>, because its rows
///     arrive on the nodes' own threads (see <see cref="AgentAssignmentTelemetry" />). It builds a
///     <see cref="TableReport" /> from a snapshot the first time a renderer asks for its cells -- at the scenario's
///     close, on the test's side -- and keeps it, so the cap and the row stamping are still
///     <see cref="TableReport" />'s.
/// </remarks>
public sealed class AgentAssignmentTimeline : IScenarioReport
{
    private readonly Lazy<Grid> _grid;

    internal AgentAssignmentTimeline(AgentAssignmentTelemetry telemetry)
    {
        Telemetry = telemetry;
        _grid = new Lazy<Grid>(build, LazyThreadSafetyMode.ExecutionAndPublication);
    }

    public AgentAssignmentTelemetry Telemetry { get; }

    public string Title => "Agent assignments over time";

    public string? ShortTitle => "Assignments";

    public ReportVisibility Visibility => ReportVisibility.OnFailure;

    public IReadOnlyList<string> Columns => _grid.Value.Columns;

    public IReadOnlyList<CellResult> Cells => _grid.Value.Cells;

    public int SuppressedRows => _grid.Value.SuppressedRows;

    private Grid build()
    {
        var grid = new Grid();
        var quiet = 0;
        TimeSpan? quietSince = null;

        void flushQuiet()
        {
            if (quiet == 0) return;
            grid.Add(quietSince, null, null, $"{quiet} evaluation(s), no changes");
            quiet = 0;
            quietSince = null;
        }

        foreach (var (at, e) in Telemetry.Snapshot())
        {
            switch (e)
            {
                case Evaluation { Commands.Count: 0 }:
                    quietSince ??= at;
                    quiet++;
                    break;

                case Evaluation evaluation:
                    flushQuiet();
                    foreach (var command in evaluation.Commands)
                    {
                        grid.Add(at, evaluation.Leader, command.Agent,
                            $"evaluation {evaluation.Number}: {command.Text}");
                    }

                    break;

                case NodeEvent node:
                    flushQuiet();
                    grid.Add(at, node.Node, null, node.Text);
                    break;

                case AgentEvent agent:
                    flushQuiet();
                    grid.Add(at, agent.Node, agent.Agent, agent.Text);
                    break;

                case TestNote note:
                    flushQuiet();
                    grid.Add(at, note.Node, null, $"(test) {note.Text}");
                    break;
            }
        }

        flushQuiet();
        return grid;
    }

    private sealed class Grid : TableReport
    {
        public override string Title => "Agent assignments over time";

        public void Add(TimeSpan? at, int? node, Uri? agent, string what)
            => Row(
                ("at", at is { } t ? $"{t.TotalSeconds:0.000}s" : ""),
                ("node", node?.ToString() ?? ""),
                ("agent", agent?.ToString() ?? ""),
                ("what", what));
    }
}
