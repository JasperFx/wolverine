using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text;
using Bobcat.Engine;
using JasperFx.Core;
using Wolverine.Runtime;
using Wolverine.Runtime.Agents;

namespace Wolverine.ComplianceTests.Telemetry;

/// <summary>
///     Everything the cluster said about leadership and agent assignments during one specification, gathered as
///     it happens and turned into Bobcat's model only when somebody reads it -- which, for a passing
///     specification, is nobody.
/// </summary>
/// <remarks>
///     <para>
///         <b>Fed from <see cref="IWolverineObserver" />, not from <c>runtime.Tracker</c>.</b> Nothing in Wolverine
///         publishes <c>AgentAssignmentsChanged</c> to the tracker any more (GH-5078 retired the dead branch of
///         <see cref="XUnitEventObserver" /> that listened for it): the tracker only carries <c>ListenerState</c>. The
///         observer is where the node controller reports what the leader decided (<c>AssignmentsChanged</c>, on
///         every evaluation, changed or not) and what each node actually did (<c>AgentStarted</c>,
///         <c>AgentStopped</c>, leadership won and lost). <see cref="Watch" /> wraps the host's observer before the
///         host starts, because the node controller captures it when it is built.
///     </para>
///     <para>
///         <b>Why it buffers instead of writing straight into a report.</b> The observer is called on each node's
///         own background loop. <c>SpecLog</c>, <c>SpecReport</c> and <c>ScenarioReports.Current</c> are
///         <c>AsyncLocal</c>, so from that thread there is no scenario to write to: the call is a silent no-op.
///         And a <see cref="TableReport" /> is a plain list, not safe to append to from several threads while a
///         renderer reads it. So the observer only enqueues, and <see cref="AgentAssignmentTimeline" /> snapshots
///         the queue on the test's side.
///     </para>
///     <para>
///         <b>Bounded.</b> A leader reports after every evaluation, once a second here, and a stuck test keeps
///         going until its timeout. The newest <see cref="MaxEvents" /> are kept and the rest counted, so the
///         transcript says how much it lost.
///     </para>
/// </remarks>
public sealed class AgentAssignmentTelemetry
{
    public const int MaxEvents = 5_000;

    private readonly ConcurrentQueue<Observed> _events = new();
    private readonly Stopwatch _clock = Stopwatch.StartNew();
    private int _dropped;
    private int _evaluations;

    public AgentAssignmentTelemetry()
    {
        Timeline = new AgentAssignmentTimeline(this);
    }

    /// <summary>The scenario report this telemetry renders as. One per specification.</summary>
    public AgentAssignmentTimeline Timeline { get; }

    public TimeSpan Elapsed => _clock.Elapsed;

    public int Dropped => _dropped;

    /// <summary>
    ///     Wrap <paramref name="runtime" />'s observer so this telemetry hears what it hears. Must be called after
    ///     the host is built and before it starts.
    /// </summary>
    public void Watch(IWolverineRuntime runtime)
    {
        runtime.Observer = new TelemetryObserver(this, runtime, runtime.Observer);
    }

    /// <summary>
    ///     Put <see cref="Timeline" /> on the scenario in progress. Called from the test's own async context; a
    ///     no-op outside a specification, and when it is already there.
    /// </summary>
    public void AttachToScenario()
    {
        if (ScenarioReports.Current is { } sink && !sink.Reports.Contains(Timeline))
        {
            sink.AttachReport(Timeline);
        }
    }

    /// <summary>A note from the test's side, so the timeline shows what the test did to the cluster.</summary>
    public void Note(int? node, string text) => record(new TestNote(node, text));

    internal void Record(ObservedEvent e)
    {
        if (e is Evaluation evaluation)
        {
            evaluation.Number = Interlocked.Increment(ref _evaluations);
        }

        record(e);
    }

    private void record(ObservedEvent e)
    {
        _events.Enqueue(new Observed(_clock.Elapsed, e));

        while (_events.Count > MaxEvents && _events.TryDequeue(out _))
        {
            Interlocked.Increment(ref _dropped);
        }
    }

    /// <summary>What has been observed so far, oldest first.</summary>
    public IReadOnlyList<Observed> Snapshot() => _events.ToArray();

    /// <summary>
    ///     Everything, in the format <see cref="XUnitEventObserver" /> was written to produce, with the time
    ///     added -- including the evaluations that changed nothing, which the timeline folds away.
    /// </summary>
    public string Transcript()
    {
        var builder = new StringBuilder();
        if (_dropped > 0)
        {
            builder.AppendLine($"({_dropped} earlier events not kept; the newest {MaxEvents} follow)");
        }

        foreach (var (at, e) in Snapshot())
        {
            var stamp = $"{at.TotalSeconds,8:0.000}s";
            switch (e)
            {
                case Evaluation evaluation:
                    builder.AppendLine(
                        $"{stamp} Node {evaluation.Leader}: evaluation {evaluation.Number} for known nodes {string.Join(", ", evaluation.Nodes)}");
                    if (evaluation.Commands.Count == 0)
                    {
                        builder.AppendLine("           No assignment changes detected");
                    }

                    foreach (var command in evaluation.Commands)
                    {
                        builder.AppendLine(command.Agent is null
                            ? $"           > {command.Text}"
                            : $"           > {command.Agent}: {command.Text}");
                    }

                    foreach (var agent in evaluation.Agents)
                    {
                        builder.AppendLine($"           * {agent.Uri} {agent.Describe()}");
                    }

                    break;

                default:
                    builder.AppendLine($"{stamp} {e}");
                    break;
            }
        }

        return builder.ToString();
    }

    public sealed record Observed(TimeSpan At, ObservedEvent Event);
}

/// <summary>One thing the cluster (or the test) did.</summary>
public abstract record ObservedEvent;

/// <summary>A node-level event: started, stopped, won or lost the leadership, ejected stale peers.</summary>
public sealed record NodeEvent(int Node, string Text) : ObservedEvent
{
    public override string ToString() => $"Node {Node}: {Text}";
}

/// <summary>What a node did with one of its own agents.</summary>
public sealed record AgentEvent(int Node, Uri Agent, string Text) : ObservedEvent
{
    public override string ToString() => $"Node {Node}: {Agent} {Text}";
}

/// <summary>Something the specification itself did to the cluster: stopped a node, paused an agent.</summary>
public sealed record TestNote(int? Node, string Text) : ObservedEvent
{
    public override string ToString() => $"Test: {Text}";
}

/// <summary>
///     One leader evaluation, copied out of the <see cref="AssignmentGrid" /> while the observer has it, because
///     the grid is the leader's working state and not the observer's to keep.
/// </summary>
public sealed record Evaluation(int Leader, IReadOnlyList<int> Nodes, IReadOnlyList<AgentPlacement> Agents,
    IReadOnlyList<AssignmentCommand> Commands) : ObservedEvent
{
    /// <summary>Counted by the telemetry across every leader, so the numbers run on through a handover.</summary>
    public int Number { get; internal set; }

    internal static Evaluation From(int leader, AssignmentGrid grid, AgentCommands commands)
    {
        var numbers = grid.Nodes.ToDictionary(x => x.NodeId, x => x.AssignedId);

        string node(Guid id) => numbers.TryGetValue(id, out var number) ? $"node {number}" : $"node {id}";

        var agents = grid.AllAgents
            .OrderBy(x => x.Uri.ToString())
            .Select(x => new AgentPlacement(x.Uri, x.OriginalNode?.AssignedId, x.AssignedNode?.AssignedId))
            .ToArray();

        var described = new List<AssignmentCommand>();
        foreach (var command in commands)
        {
            switch (command)
            {
                case AssignAgent a:
                    described.Add(new AssignmentCommand(a.AgentUri, $"start on {node(a.Destination.NodeId)}"));
                    break;
                case AssignAgents a:
                    described.AddRange(a.AgentIds.Select(uri =>
                        new AssignmentCommand(uri, $"start on {node(a.Destination.NodeId)}")));
                    break;
                case ReassignAgent r:
                    described.Add(new AssignmentCommand(r.AgentUri,
                        $"move from {node(r.OriginalNode.NodeId)} to {node(r.ActiveNode.NodeId)}"));
                    break;
                case ReassignAgents r:
                    described.AddRange(r.AgentUris.Select(uri => new AssignmentCommand(uri,
                        $"move from {node(r.OriginalNode.NodeId)} to {node(r.ActiveNode.NodeId)}")));
                    break;
                case StopRemoteAgent s:
                    described.Add(new AssignmentCommand(s.AgentUri, $"stop on {node(s.Destination.NodeId)}"));
                    break;
                case StopRemoteAgents s:
                    described.AddRange(s.AgentIds.Select(uri =>
                        new AssignmentCommand(uri, $"stop on {node(s.Destination.NodeId)}")));
                    break;
                default:
                    described.Add(new AssignmentCommand(null, command.ToString() ?? command.GetType().Name));
                    break;
            }
        }

        return new Evaluation(leader, grid.Nodes.Select(x => x.AssignedId).OrderBy(x => x).ToArray(), agents,
            described);
    }
}

/// <summary>One agent's line in an evaluation: where it was, and where the leader wants it.</summary>
public sealed record AgentPlacement(Uri Uri, int? OriginalNode, int? AssignedNode)
{
    /// <summary>Worded as <see cref="XUnitEventObserver" /> words it.</summary>
    public string Describe() => this switch
    {
        { AssignedNode: null } => "is not assigned",
        { OriginalNode: null } => $"assigned to node {AssignedNode}",
        _ when OriginalNode == AssignedNode => $"is unchanged on node {AssignedNode}",
        _ => $"reassigned from node {OriginalNode} to node {AssignedNode}"
    };
}

/// <summary>One command an evaluation issued, with the agent it is about when it is about one.</summary>
public sealed record AssignmentCommand(Uri? Agent, string Text);
