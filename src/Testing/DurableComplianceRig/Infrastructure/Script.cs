using System.Collections.Concurrent;

namespace DurableComplianceRig.Infrastructure;

/// <summary>A single message the rig sends. Every handler in the rig records it in the ledger.</summary>
public record RigMessage(Guid Id);

/// <summary>A message the rig batches with <c>BatchMessagesOf&lt;RigItem&gt;</c>; handled as <c>RigItem[]</c>.</summary>
public record RigItem(Guid Id);

/// <summary>The failure the rig's error policies are written against.</summary>
public class RigFailure(string message) : Exception(message);

public enum StepKind
{
    Succeed,

    /// <summary>Throw <see cref="RigFailure" /> before writing anything.</summary>
    Throw,

    /// <summary>Write the ledger row inside the handler's transaction, then throw before it commits.</summary>
    ThrowAfterLedgerWrite,

    /// <summary>Wait, then succeed. Long enough to straddle a drain, a pause or a stale sweep.</summary>
    Delay,

    /// <summary>Wait until the scenario opens the named gate, then succeed.</summary>
    Gate
}

public record Step(StepKind Kind, TimeSpan Delay = default, string? GateName = null)
{
    public static readonly Step Succeed = new(StepKind.Succeed);
    public static readonly Step Throw = new(StepKind.Throw);
    public static readonly Step ThrowAfterLedgerWrite = new(StepKind.ThrowAfterLedgerWrite);
    public static Step Wait(TimeSpan delay) => new(StepKind.Delay, delay);
    public static Step WaitForGate(string name) => new(StepKind.Gate, GateName: name);
}

/// <summary>
/// What a handler does on each attempt at a message. Keyed by message id; attempt N runs step N-1, and the
/// last step repeats. A message with no script just succeeds.
///
/// <para>
/// Static because handlers are code generated and scenarios run one at a time (see Usings.cs). Every
/// execution is also counted and timed here, independently of the ledger, so the checker can tell "the
/// handler ran twice" apart from "the handler committed twice".
/// </para>
/// </summary>
public static class Script
{
    private static readonly ConcurrentDictionary<Guid, Step[]> _steps = new();
    private static readonly ConcurrentDictionary<Guid, int> _executions = new();
    private static readonly ConcurrentDictionary<string, TaskCompletionSource> _gates = new();
    private static readonly ConcurrentQueue<Execution> _log = new();

    public static void Reset()
    {
        _steps.Clear();
        _executions.Clear();
        foreach (var gate in _gates.Values) gate.TrySetResult();
        _gates.Clear();
        _log.Clear();
    }

    public static void Set(Guid id, params Step[] steps) => _steps[id] = steps;

    public static void OpenGate(string name) => gateFor(name).TrySetResult();

    public static int ExecutionsOf(Guid id) => _executions.TryGetValue(id, out var count) ? count : 0;

    public static IReadOnlyList<Execution> Log => _log.ToArray();

    private static TaskCompletionSource gateFor(string name) =>
        _gates.GetOrAdd(name, _ => new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously));

    /// <summary>Called by every rig handler before it touches the ledger. Returns the step to finish with.</summary>
    /// <param name="envelopeId">The envelope that carried the message, or null when it rode a batch carrier</param>
    public static async Task<(Step step, Execution execution)> BeginAsync(Guid id, string node, Guid? envelopeId = null)
    {
        var attempt = _executions.AddOrUpdate(id, 1, (_, n) => n + 1);
        var steps = _steps.TryGetValue(id, out var s) ? s : [Step.Succeed];
        var step = steps[Math.Min(attempt, steps.Length) - 1];

        var execution = new Execution(id, attempt, node, DateTimeOffset.UtcNow) { EnvelopeId = envelopeId };
        _log.Enqueue(execution);

        switch (step.Kind)
        {
            case StepKind.Throw:
                execution.Finish("threw");
                throw new RigFailure($"Scripted failure of {id} on attempt {attempt}");

            case StepKind.Delay:
                await Task.Delay(step.Delay);
                break;

            case StepKind.Gate:
                await gateFor(step.GateName!).Task.WaitAsync(TimeSpan.FromMinutes(2));
                break;
        }

        return (step, execution);
    }

    /// <summary>Called after the ledger write, still inside the handler's transaction.</summary>
    public static void BeforeCommit(Step step, Execution execution)
    {
        if (step.Kind == StepKind.ThrowAfterLedgerWrite)
        {
            execution.Finish("threw after ledger write");
            throw new RigFailure($"Scripted failure of {execution.MessageId} after its ledger write");
        }
    }
}

/// <summary>One run of a handler, as the handler itself saw it.</summary>
public class Execution(Guid messageId, int attempt, string node, DateTimeOffset startedAt)
{
    public Guid MessageId { get; } = messageId;
    public int Attempt { get; } = attempt;
    public string Node { get; } = node;
    public DateTimeOffset StartedAt { get; } = startedAt;
    public Guid? EnvelopeId { get; init; }
    public DateTimeOffset? FinishedAt { get; private set; }
    public string? Outcome { get; private set; }

    public void Finish(string outcome)
    {
        FinishedAt = DateTimeOffset.UtcNow;
        Outcome = outcome;
    }

    public bool Overlaps(Execution other) =>
        StartedAt < (other.FinishedAt ?? DateTimeOffset.MaxValue) && other.StartedAt < (FinishedAt ?? DateTimeOffset.MaxValue);

    public override string ToString() =>
        $"{MessageId} attempt {Attempt} on {Node} {StartedAt:HH:mm:ss.fff}..{FinishedAt:HH:mm:ss.fff} {Outcome}";
}
