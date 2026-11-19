using System.ComponentModel;
using Wolverine.Persistence.Sagas;

// ReSharper disable once CheckNamespace
namespace Wolverine;

/// <summary>
/// GH-5556. A <see cref="Saga" /> that shows its compensation: its <see cref="Status" /> and a short
/// <see cref="CompensationLog" />, both kept in the saga body. Any <see cref="Saga" /> may declare <c>Compensate</c>
/// methods; this base type only adds the visible state. A saga that does not derive from it stores exactly what it
/// stores today.
/// </summary>
/// <remarks>
/// <para>
/// A concurrency conflict saving the compensated saga -- another message saved it meanwhile -- is retried under the
/// compensation's error policies. The lightweight saga tables always check the saga's version; on Marten, have the saga
/// implement <c>JasperFx.IRevisioned</c> as well.
/// </para>
/// <para>
/// GH-5673. It also keeps an undo stack. <see cref="PushUndo" /> records an undo message as the saga goes; a
/// <c>Compensate</c> method unwinds it with <see cref="UndoMessages(bool)" />, <see cref="UndoMessagesSince" />, a
/// filter, or <see cref="UndoSequentially" />, which sends one undo message at a time and waits for each to be
/// confirmed. Push an undo before its step, and keep undo messages idempotent: a step whose outcome is unknown is undone
/// too. Nothing unwinds by itself: with no <c>Compensate</c> method matching a failure, the stack is left alone.
/// </para>
/// </remarks>
public abstract class CompensatingSaga : Saga
{
    /// <summary>Where the saga is in its compensation, if anywhere. Set by Wolverine.</summary>
    public SagaStatus Status { get; set; } = SagaStatus.Active;

    /// <summary>One entry per compensation Wolverine ran, oldest first. Written by Wolverine.</summary>
    public List<CompensationLogEntry> CompensationLog { get; set; } = [];

    /// <summary>GH-5673. The undo messages pushed so far, oldest first. Written by Wolverine.</summary>
    public List<UndoEntry> UndoStack { get; set; } = [];

    /// <summary>GH-5673. A sequential unwind in progress, if there is one. Written by Wolverine.</summary>
    public SagaUnwind? Unwinding { get; set; }

    // ---- what one handler did to the stack. Never stored --------------------------------------------------------------

    // An event-sourced saga's state comes only from its events: this handler's changes are made to a copy of the stack
    // and kept as Wolverine's own events, which Wolverine appends to the saga's stream after the handler
    private List<UndoEntry>? _working;
    private List<object>? _unsaved;
    private List<UndoEntry>? _pushed;
    private List<UndoneStep>? _undone;
    private List<object>? _undoMessages;
    private HashSet<Guid>? _confirmed;

    private bool recordsEvents => this is EventSourcedSaga;

    // The stack as this handler sees it
    private List<UndoEntry> stack => recordsEvents ? _working ??= UndoStack.Select(x => x.Clone()).ToList() : UndoStack;

    /// <summary>
    /// GH-5673. Push <paramref name="undoMessage" /> onto the undo stack. Pushed in a handler that also sends a message,
    /// it is pending until that step's reply confirms it (or <see cref="ConfirmUndo" /> does); pushed in a handler that
    /// sends nothing, it is confirmed at once. An undo pushed after its step, in a handler that sends the next step, is
    /// therefore pending until that next step replies: call <see cref="ConfirmUndo" /> in the same handler to confirm it.
    /// </summary>
    /// <param name="undoMessage">
    /// The message that undoes the step. Kept with the saga's state as System.Text.Json plus its .NET type name, so it
    /// must round trip through System.Text.Json
    /// </param>
    /// <param name="step">An optional label, for <see cref="ConfirmUndo" />, <see cref="UndoMessagesSince" /> and the log</param>
    /// <param name="confirmedBy">
    /// In a sequential unwind, a business reply the undo message's handler returns, such as <c>FlightCancelled</c>, that
    /// confirms the undo instead of Wolverine's acknowledgement
    /// </param>
    protected void PushUndo(object undoMessage, string? step = null, Type? confirmedBy = null)
    {
        var entry = UndoEntry.For(undoMessage, step, confirmedBy);
        (_pushed ??= []).Add(entry);
        record(new SagaUndoPushed(entry));
    }

    /// <summary>
    /// GH-5673. The event an event-sourced saga's static <c>Start</c> returns to push an undo message, since there is no
    /// saga yet to call <see cref="PushUndo" /> on. Return it among the start's events.
    /// </summary>
    protected static SagaUndoPushed PushedUndo(object undoMessage, string? step = null, Type? confirmedBy = null)
        => new(UndoEntry.For(undoMessage, step, confirmedBy));

    /// <summary>
    /// GH-5673. Confirm the pending undo entries pushed under <paramref name="step" />, for a step whose reply Wolverine
    /// cannot tie to the request.
    /// </summary>
    protected void ConfirmUndo(string step)
    {
        foreach (var entry in stack.Where(x => x.Step == step && (x.Pending || PushedNow.Contains(x))).ToArray())
        {
            (_confirmed ??= []).Add(entry.Id);
            record(new SagaUndoConfirmed(entry.Id));
        }
    }

    /// <summary>
    /// GH-5673. Every undo message on the stack, newest first, sent together. The documented default for a
    /// <c>Compensate</c> method. The entries leave the stack.
    /// </summary>
    /// <param name="includePending">Undo the steps never confirmed as well: their outcome is unknown</param>
    protected OutgoingMessages UndoMessages(bool includePending = true)
        => undo(x => includePending || !x.Pending);

    /// <summary>
    /// GH-5673. The undo messages pushed under <paramref name="step" /> and after it, newest first: a partial unwind
    /// </summary>
    protected OutgoingMessages UndoMessagesSince(string step, bool includePending = true)
    {
        var from = stack.FindLastIndex(x => x.Step == step);
        if (from < 0) return [];

        var since = stack.Skip(from).Select(x => x.Id).ToHashSet();
        return undo(x => since.Contains(x.Id) && (includePending || !x.Pending));
    }

    /// <summary>GH-5673. The undo messages <paramref name="filter" /> picks, newest first</summary>
    protected OutgoingMessages UndoMessages(Func<UndoEntry, bool> filter) => undo(filter);

    /// <summary>
    /// GH-5673. Unwind the stack one undo message at a time, newest first: each is sent asking for Wolverine's
    /// acknowledgement (or the business reply it was pushed with) and the next goes only when it is confirmed. The saga
    /// is <see cref="SagaStatus.Compensating" /> meanwhile, then <see cref="SagaStatus.Compensated" /> once the stack is
    /// empty. An undo message that fails for good leaves the saga <see cref="SagaStatus.CompensationFailed" /> with the
    /// rest of the stack in place. Return it from a <c>Compensate</c> method.
    /// </summary>
    /// <param name="includePending">Undo the steps never confirmed as well: their outcome is unknown</param>
    /// <param name="completeWhenDone">
    /// Complete the saga once the last undo message is confirmed (an event-sourced saga gets a
    /// <see cref="SagaCompleted" /> event). By default the saga stays open, <see cref="SagaStatus.Compensated" />. Don't
    /// complete it yourself while it unwinds: the confirmations would find no saga, and the unwind would stop
    /// </param>
    protected UndoSequence UndoSequentially(bool includePending = true, bool completeWhenDone = false)
    {
        var sequence = new UndoSequence(includePending, completeWhenDone);
        if (NextSequentialUndo(includePending) is { } first) sequence.Add(first);
        return sequence;
    }

    /// <summary>Not called by application code: the method Wolverine files a saga's sequential-unwind replies under</summary>
    [EditorBrowsable(EditorBrowsableState.Never)]
    public void ContinueUnwinding(UndoReply reply)
        => throw new NotSupportedException("Wolverine continues a sequential unwind itself");

    private OutgoingMessages undo(Func<UndoEntry, bool> filter)
    {
        var messages = new OutgoingMessages();
        foreach (var entry in stack.Where(x => !x.Undoing).Where(filter).Reverse().ToArray())
        {
            var message = entry.ToMessage();
            messages.Add(message);
            (_undoMessages ??= []).Add(message);
            (_undone ??= []).Add(entry.ToUndone());
            record(new SagaUndoSent(entry.Id, false));
        }

        return messages;
    }

    // The newest entry a sequential unwind has not sent yet, sent asking to be confirmed
    internal object? NextSequentialUndo(bool includePending)
    {
        var entry = stack.LastOrDefault(x => !x.Undoing && (includePending || !x.Pending));
        if (entry == null) return null;

        var message = entry.ToMessage();
        (_undoMessages ??= []).Add(message);
        record(new SagaUndoSent(entry.Id, true));

        return SagaUndo.SendForConfirmation(this, entry, message);
    }

    // ---- Wolverine's bookkeeping --------------------------------------------------------------------------------------

    /// <summary>
    /// Forget what the last handler did to the stack. A saga kept in memory is the same object from one message to the
    /// next
    /// </summary>
    internal void BeginHandling()
    {
        _working = null;
        _unsaved = null;
        _pushed = null;
        _undone = null;
        _undoMessages = null;
        _confirmed = null;
    }

    /// <summary>
    /// GH-5673. A sequential unwind asked to complete the saga when it ended: completed now, or for an event-sourced
    /// saga, a <see cref="SagaCompleted" /> event for its stream
    /// </summary>
    internal void CompleteAfterUnwind()
    {
        if (recordsEvents)
        {
            (_unsaved ??= []).Add(new SagaCompleted());
        }
        else
        {
            MarkCompleted();
        }
    }

    /// <summary>The entries pushed during this handler</summary>
    internal IReadOnlyList<UndoEntry> PushedNow => _pushed ?? (IReadOnlyList<UndoEntry>)[];

    /// <summary>Did this handler confirm the entry itself, with <see cref="ConfirmUndo" />?</summary>
    internal bool ConfirmedNow(Guid entryId) => _confirmed?.Contains(entryId) == true;

    /// <summary>Is this one of the undo messages this handler sent, which are not steps of their own?</summary>
    internal bool IsUndoMessage(object? message)
        => message != null && _undoMessages?.Any(x => ReferenceEquals(x, message)) == true;

    /// <summary>The undo messages this handler sent, with their step tags, taken once</summary>
    internal List<UndoneStep>? TakeUndone()
    {
        var undone = _undone;
        _undone = null;
        return undone;
    }

    /// <summary>The entry with this id, as this handler sees the stack</summary>
    internal UndoEntry? FindUndo(Guid id) => stack.FirstOrDefault(x => x.Id == id);

    /// <summary>The newest entry a sequential unwind is still waiting on, or has yet to send</summary>
    internal bool HasUndoLeft(bool includePending) => stack.Any(x => includePending || !x.Pending);

    /// <summary>
    /// Record a change to the saga's compensation state as Wolverine's own event: applied at once to a document saga,
    /// kept for the stream by an event-sourced one
    /// </summary>
    internal void Record(object frameworkEvent) => record(frameworkEvent);

    private void record(object frameworkEvent)
    {
        if (recordsEvents)
        {
            ApplyCompensationEvent(stack, frameworkEvent, DateTimeOffset.UtcNow, state: null);
            (_unsaved ??= []).Add(frameworkEvent);
        }
        else
        {
            ApplyCompensationEvent(UndoStack, frameworkEvent, DateTimeOffset.UtcNow, this);
        }
    }

    /// <summary>The events an event-sourced saga's handler produced, taken once to be appended to its stream</summary>
    internal IReadOnlyList<object> TakeUnsavedEvents()
    {
        var events = _unsaved ?? [];
        _unsaved = null;
        return events;
    }

    /// <summary>
    /// Apply one of Wolverine's own compensation events. <paramref name="state" /> is the saga whose status, log and
    /// unwind change, or null to change only <paramref name="undoStack" />
    /// </summary>
    internal static void ApplyCompensationEvent(List<UndoEntry> undoStack, object frameworkEvent, DateTimeOffset at,
        CompensatingSaga? state)
    {
        switch (frameworkEvent)
        {
            case SagaUndoPushed pushed:
                if (undoStack.All(x => x.Id != pushed.Entry.Id)) undoStack.Add(pushed.Entry);
                break;

            case SagaUndoConfirmed confirmed:
                if (undoStack.FirstOrDefault(x => x.Id == confirmed.EntryId) is { } toConfirm) toConfirm.Pending = false;
                break;

            case SagaUndoSent sent:
                if (sent.AwaitsConfirmation)
                {
                    if (undoStack.FirstOrDefault(x => x.Id == sent.EntryId) is { } inFlight) inFlight.Undoing = true;
                }
                else
                {
                    undoStack.RemoveAll(x => x.Id == sent.EntryId);
                }

                break;

            case SagaUndoCompleted completed:
                if (undoStack.FirstOrDefault(x => x.Id == completed.EntryId) is { } done)
                {
                    undoStack.Remove(done);
                    state?.Unwinding?.Undone.Add(done.ToUndone());
                }

                break;

            case SagaUndoFailed failed:
                if (undoStack.FirstOrDefault(x => x.Id == failed.EntryId) is { } stuck) stuck.Undoing = false;
                break;

            case SagaCompensating compensating when state != null:
                state.Status = SagaStatus.Compensating;
                state.Unwinding = new SagaUnwind
                {
                    Step = compensating.Step,
                    ExceptionType = compensating.ExceptionType,
                    FailedMessageId = compensating.FailedMessageId,
                    IncludePending = compensating.IncludePending,
                    CompleteWhenDone = compensating.CompleteWhenDone
                };
                break;

            case SagaCompensated compensated when state != null:
                state.Status = SagaStatus.Compensated;
                state.log(compensated.Step, compensated.ExceptionType, CompensationOutcome.Compensated, at,
                    compensated.Undone);
                break;

            case SagaCompensationFailed compensationFailed when state != null:
                state.Status = SagaStatus.CompensationFailed;
                state.log(compensationFailed.Step, compensationFailed.ExceptionType, CompensationOutcome.Failed, at,
                    compensationFailed.Undone);
                break;
        }
    }

    private void log(string step, string exceptionType, CompensationOutcome outcome, DateTimeOffset at,
        List<UndoneStep>? undone)
    {
        var unwind = Unwinding;
        CompensationLog ??= [];
        CompensationLog.Add(new CompensationLogEntry(step, exceptionType, outcome, at)
        {
            FailedMessageId = unwind?.FailedMessageId,
            Undone = undone ?? (unwind?.Undone is { Count: > 0 } sofar ? [..sofar] : null)
        });
        Unwinding = null;
    }
}

/// <summary>GH-5556. Where a <see cref="CompensatingSaga" /> is in its compensation.</summary>
public enum SagaStatus
{
    /// <summary>Nothing has been compensated</summary>
    Active,

    /// <summary>A compensation is in progress: a sequential unwind waiting for its undo messages to be confirmed</summary>
    Compensating,

    /// <summary>The last compensation succeeded</summary>
    Compensated,

    /// <summary>The last compensation failed, after its retries</summary>
    CompensationFailed
}

/// <summary>GH-5556. How one compensation ended.</summary>
public enum CompensationOutcome
{
    Compensated,
    Failed
}

/// <summary>
/// GH-5556. One compensation: the failed step (Wolverine's message type name of the failed message), the full name of
/// the exception type that was actually thrown, the outcome and when it happened.
/// </summary>
public record CompensationLogEntry(string Step, string ExceptionType, CompensationOutcome Outcome, DateTimeOffset At)
{
    /// <summary>GH-5673. The envelope id of the failed message, when known</summary>
    public Guid? FailedMessageId { get; init; }

    /// <summary>GH-5673. The undo messages the compensation sent, with their step tags. Null when it sent none</summary>
    public List<UndoneStep>? Undone { get; init; }
}
