using Wolverine;
using System.Text.Json;
using JasperFx.Events;

namespace MartenTests.Workflows.Engine;

public enum ReplayOutcome
{
    /// <summary>The method reached a wait the journal can't answer yet</summary>
    Suspended,
    Completed,

    /// <summary>The code asked for something the journal doesn't have at that point</summary>
    Faulted,

    /// <summary>The workflow method threw</summary>
    Failed
}

public record OutgoingWorkflowMessage(object Message, DeliveryOptions Options, bool Publish);

/// <summary>
/// GH-5654 SPIKE. One run of a workflow method against its journal. The method runs from the top; every
/// <see cref="IWorkflowContext" /> call that the journal already answers returns the stored result with no side
/// effect, and the first one it can't answer either does its effect (sends and recorded values carry on) or
/// suspends the method (waits).
///
/// Matching rules:
/// - positional: sends (including the scheduled timeout/delay messages behind WaitForAsync/DelayAsync), recorded
///   values and OnFailure registrations, in the order the code makes them
/// - by type, in arrival order: received messages; a message that arrived before the code waited for it is
///   simply the first unconsumed one of its type when the code gets there
/// - by step: a timeout or an elapsed delay names the positional step that scheduled it
/// </summary>
public class WorkflowReplay : IWorkflowContext
{
    private readonly WorkflowDefinition _definition;
    private readonly IReadOnlyList<IEvent> _journal;
    private readonly Func<object, IEvent> _buildEvent;

    // Indexes into _journal
    private readonly List<int> _positional = new();
    private readonly List<int> _received = new();
    private readonly HashSet<int> _consumed = new();
    private readonly Dictionary<int, int> _timedOut = new();
    private readonly Dictionary<int, int> _delayElapsed = new();

    private readonly TaskCompletionSource _halted = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private int _step;

    public WorkflowReplay(Guid workflowId, WorkflowDefinition definition, IReadOnlyList<IEvent> journal,
        Func<object, IEvent> buildEvent)
    {
        WorkflowId = workflowId;
        _definition = definition;
        _journal = journal;
        _buildEvent = buildEvent;

        for (var i = 0; i < journal.Count; i++)
        {
            var e = journal[i];
            switch (e.Data)
            {
                case WorkflowValueRecorded or WorkflowFailureRegistered:
                    _positional.Add(i);
                    continue;
                case WorkflowTimedOut timedOut:
                    _timedOut[timedOut.Step] = i;
                    continue;
                case WorkflowDelayElapsed elapsed:
                    _delayElapsed[elapsed.Step] = i;
                    continue;
            }

            switch (e.WorkflowRole())
            {
                case WorkflowHeaders.Sent:
                    _positional.Add(i);
                    break;
                case WorkflowHeaders.Received:
                    _received.Add(i);
                    break;
            }
        }

        Trigger = journal.First(x => x.WorkflowRole() == WorkflowHeaders.Trigger).Data;
    }

    public Guid WorkflowId { get; }
    public object Trigger { get; }

    /// <summary>Events this run made in new territory, to append to the stream</summary>
    public List<IEvent> NewEvents { get; } = new();

    /// <summary>Messages this run sent in new territory, to put in the outbox</summary>
    public List<OutgoingWorkflowMessage> Outgoing { get; } = new();

    public ReplayOutcome Outcome { get; private set; }
    public string? FaultReason { get; private set; }
    public Exception? Failure { get; private set; }
    public string? WaitingOn { get; private set; }

    /// <summary>
    /// Every OnFailure registration this run made, replayed or new, in order. If the run failed, this is what
    /// compensation sends (in reverse), including registrations the failed run made that never reached the journal.
    /// </summary>
    public List<object> Registrations { get; } = new();

    /// <summary>How many recorded steps the replay walked past before doing anything new</summary>
    public int ReplayedSteps { get; private set; }

    public async Task RunAsync(IServiceProvider services)
    {
        Task run;
        try
        {
            run = _definition.InvokeAsync(Trigger, this, services);
        }
        catch (Exception e)
        {
            run = Task.FromException(e);
        }

        // A suspended or faulted method is left awaiting a task that never completes. It is never resumed in
        // memory: the next message replays it from the top.
        var finished = await Task.WhenAny(run, _halted.Task);

        if (FaultReason != null)
        {
            Outcome = ReplayOutcome.Faulted;
            return;
        }

        if (finished == _halted.Task)
        {
            Outcome = ReplayOutcome.Suspended;
            return;
        }

        if (run.IsFaulted)
        {
            Outcome = ReplayOutcome.Failed;
            Failure = run.Exception!.InnerException;
            return;
        }

        if (_step < _positional.Count)
        {
            fault($"finishes", entryAt(_step));
            Outcome = ReplayOutcome.Faulted;
            return;
        }

        Outcome = ReplayOutcome.Completed;
    }

    public ValueTask SendAsync(object message, DeliveryOptions? options = null)
        => send(message, options, false, null);

    public ValueTask PublishAsync(object message, DeliveryOptions? options = null)
        => send(message, options, true, null);

    public async Task<TReply> InvokeAsync<TReply>(object request, DeliveryOptions? options = null) where TReply : class
    {
        await send(request, options, false, typeof(TReply).Name);
        return await receive<TReply>();
    }

    public Task<T?> WaitForAsync<T>(TimeSpan timeout) where T : class
    {
        if (tryReplay(out var entry))
        {
            if (entry.WorkflowRole() == WorkflowHeaders.Sent && entry.Data is WorkflowTimeout scheduled)
            {
                if (scheduled.WaitingFor != typeof(T).Name)
                {
                    return faulted<T?>($"waits for {typeof(T).Name}", entry);
                }

                // The original run had to wait here. Whichever came first answers it.
                var step = _step++;
                ReplayedSteps++;
                var received = firstUnconsumed<T>();
                var timedOut = _timedOut.TryGetValue(step, out var index) ? index : -1;

                if (received >= 0 && (timedOut < 0 || received < timedOut))
                {
                    return Task.FromResult<T?>(consume<T>(received));
                }

                if (timedOut >= 0) return Task.FromResult<T?>(null);

                return suspend<T?>($"WaitForAsync<{typeof(T).Name}>");
            }

            // The original run did not wait here, so a T was already in the journal when it got here
            var early = firstUnconsumed<T>();
            if (early >= 0) return Task.FromResult<T?>(consume<T>(early));

            return faulted<T?>($"waits for {typeof(T).Name}", entry);
        }

        // New territory. A T that arrived before the workflow waited for it answers immediately.
        var available = firstUnconsumed<T>();
        if (available >= 0) return Task.FromResult<T?>(consume<T>(available));

        var timeoutMessage = new WorkflowTimeout(WorkflowId, _step, typeof(T).Name);
        recordSend(timeoutMessage, new DeliveryOptions { ScheduleDelay = timeout }, false, null);

        return suspend<T?>($"WaitForAsync<{typeof(T).Name}>");
    }

    public Task DelayAsync(TimeSpan delay)
    {
        if (tryReplay(out var entry))
        {
            if (entry.WorkflowRole() != WorkflowHeaders.Sent || entry.Data is not WorkflowDelay)
            {
                return faulted<bool>("delays", entry);
            }

            var step = _step++;
            ReplayedSteps++;
            return _delayElapsed.ContainsKey(step) ? Task.CompletedTask : suspend<bool>("DelayAsync");
        }

        recordSend(new WorkflowDelay(WorkflowId, _step), new DeliveryOptions { ScheduleDelay = delay }, false, null);
        return suspend<bool>("DelayAsync");
    }

    public DateTimeOffset Now() => recordedValue("Now", () => DateTimeOffset.UtcNow);

    public Guid NewGuid() => recordedValue("NewGuid", Guid.NewGuid);

    public async Task<T> RunAsync<T>(Func<Task<T>> action)
    {
        if (tryReplay(out var entry))
        {
            return readRecordedValue<T>("RunAsync", entry);
        }

        // At least once: the side effect runs again if this resume fails before it commits
        var step = _step;
        var result = await action();
        NewEvents.Add(_buildEvent(new WorkflowValueRecorded(step, "RunAsync", JsonSerializer.Serialize(result))));
        _step++;
        return result;
    }

    public void OnFailure(object undoMessage)
    {
        var messageType = typeNameFor(undoMessage.GetType());
        Registrations.Add(undoMessage);
        if (tryReplay(out var entry))
        {
            if (entry.Data is not WorkflowFailureRegistered registered || registered.MessageType != messageType)
            {
                fault($"registers OnFailure({undoMessage.GetType().Name})", entry);
                throw new WorkflowReplayMismatchException(FaultReason!);
            }

            _step++;
            ReplayedSteps++;
            return;
        }

        NewEvents.Add(_buildEvent(new WorkflowFailureRegistered(_step, messageType,
            JsonSerializer.Serialize(undoMessage, undoMessage.GetType()))));
        _step++;
    }

    internal static string typeNameFor(Type type) => $"{type.FullName}, {type.Assembly.GetName().Name}";

    private ValueTask send(object message, DeliveryOptions? options, bool publish, string? awaits)
    {
        if (tryReplay(out var entry))
        {
            if (entry.WorkflowRole() != WorkflowHeaders.Sent || entry.Data.GetType() != message.GetType())
            {
                return new ValueTask(faulted<bool>($"sends {message.GetType().Name}", entry));
            }

            _step++;
            ReplayedSteps++;
            return ValueTask.CompletedTask;
        }

        recordSend(message, options ?? new DeliveryOptions(), publish, awaits);
        return ValueTask.CompletedTask;
    }

    private void recordSend(object message, DeliveryOptions options, bool publish, string? awaits)
    {
        var e = _buildEvent(message)
            .WithHeader(WorkflowHeaders.Role, WorkflowHeaders.Sent)
            .WithHeader(WorkflowHeaders.Step, _step.ToString());

        if (awaits != null) e.SetHeader(WorkflowHeaders.Awaits, awaits);

        // Replies come back to this workflow through the saga-id header
        options.SagaId = WorkflowId.ToString();

        NewEvents.Add(e);
        Outgoing.Add(new OutgoingWorkflowMessage(message, options, publish));
        _step++;
    }

    private Task<T> receive<T>() where T : class
    {
        var index = firstUnconsumed<T>();
        return index >= 0 ? Task.FromResult(consume<T>(index)) : suspend<T>(typeof(T).Name);
    }

    private T recordedValue<T>(string kind, Func<T> create)
    {
        if (tryReplay(out var entry)) return readRecordedValue<T>(kind, entry);

        var value = create();
        NewEvents.Add(_buildEvent(new WorkflowValueRecorded(_step, kind, JsonSerializer.Serialize(value))));
        _step++;
        return value;
    }

    private T readRecordedValue<T>(string kind, IEvent entry)
    {
        if (entry.Data is not WorkflowValueRecorded recorded || recorded.Kind != kind)
        {
            fault($"records {kind}()", entry);
            throw new WorkflowReplayMismatchException(FaultReason!);
        }

        _step++;
        ReplayedSteps++;
        return JsonSerializer.Deserialize<T>(recorded.Json)!;
    }

    private bool tryReplay(out IEvent entry)
    {
        if (_step < _positional.Count)
        {
            entry = entryAt(_step);
            return true;
        }

        entry = null!;
        return false;
    }

    private IEvent entryAt(int step) => _journal[_positional[step]];

    private int firstUnconsumed<T>()
    {
        foreach (var index in _received)
        {
            if (_journal[index].Data is T && !_consumed.Contains(index)) return index;
        }

        return -1;
    }

    private T consume<T>(int index)
    {
        _consumed.Add(index);
        return (T)_journal[index].Data;
    }

    private Task<T> suspend<T>(string waitingOn)
    {
        WaitingOn = waitingOn;
        _halted.TrySetResult();
        return new TaskCompletionSource<T>().Task;
    }

    private Task<T> faulted<T>(string codeAction, IEvent entry)
    {
        fault(codeAction, entry);
        return new TaskCompletionSource<T>().Task;
    }

    private void fault(string codeAction, IEvent entry)
    {
        FaultReason ??=
            $"Workflow {_definition} ({WorkflowId}) no longer matches its journal: at step {_step} the code {codeAction}, " +
            $"but the journal recorded {describe(entry)} (stream version {entry.Version}). " +
            "The workflow definition changed under this running instance.";
        _halted.TrySetResult();
    }

    private static string describe(IEvent e)
    {
        return e.Data switch
        {
            WorkflowTimeout t => $"WaitForAsync<{t.WaitingFor}>",
            WorkflowDelay => "DelayAsync",
            WorkflowValueRecorded v => $"a recorded {v.Kind}()",
            WorkflowFailureRegistered f => $"OnFailure({f.MessageType.Split(',')[0].Split('.').Last()})",
            _ => $"a send of {e.Data.GetType().Name}"
        };
    }
}
