using System.Collections.Concurrent;
using System.ComponentModel;
using System.Diagnostics.CodeAnalysis;
using JasperFx.CodeGeneration;
using JasperFx.CodeGeneration.Frames;
using JasperFx.CodeGeneration.Model;
using JasperFx.Core.Reflection;
using JasperFx.Events;
using Microsoft.Extensions.Logging;
using Wolverine.Runtime;
using Wolverine.Util;

namespace Wolverine.Persistence.Sagas;

/// <summary>
/// GH-5673. The undo stack of a <see cref="CompensatingSaga" />: called by generated saga code and by Wolverine's
/// messaging. Not for application code.
/// </summary>
[EditorBrowsable(EditorBrowsableState.Never)]
public static class SagaUndo
{
    /// <summary>
    /// On a step a saga sent with undo entries pending: the ids of those entries. Carried back on the step's replies,
    /// which confirm them
    /// </summary>
    public const string StepHeader = "wolverine.undo.step";

    /// <summary>On an undo message of a sequential unwind: the saga type and the undo entry, to confirm it by</summary>
    public const string EntryHeader = "wolverine.undo.entry";

    private static readonly ConcurrentDictionary<string, Type> _replyTypes = new(StringComparer.Ordinal);

    /// <summary>The message a saga type's sequential unwind hears back through</summary>
    [UnconditionalSuppressMessage("AOT", "IL3050", Justification = "UndoReply<T> closed over a saga type rooted by handler discovery.")]
    internal static Type ReplyTypeFor(Type sagaType)
    {
        var replyType = typeof(UndoReply<>).MakeGenericType(sagaType);
        _replyTypes[sagaType.FullName!] = replyType;
        return replyType;
    }

    // ---- correlating a step's reply to the undo entries pushed before it ------------------------------------------------

    /// <summary>Before a saga's handler: the message is a step's reply, and confirms the undo entries pushed before it</summary>
    public static void BeforeHandling(CompensatingSaga? saga, MessageContext context)
    {
        saga?.BeginHandling();

        var envelope = context.Envelope;
        if (saga == null || envelope is not { HasHeaders: true } || envelope.Message == null) return;
        if (!envelope.Headers.TryGetValue(StepHeader, out var ids) || string.IsNullOrEmpty(ids)) return;

        // A step that failed confirms nothing
        if (SagaCompensations.FaultMessageType(envelope.Message.GetType()) != null) return;

        foreach (var raw in ids.Split(','))
        {
            if (Guid.TryParse(raw, out var id) && saga.FindUndo(id) is { Pending: true })
            {
                saga.Record(new SagaUndoConfirmed(id));
            }
        }
    }

    /// <summary>
    /// After a document saga's handler: the undo entries it pushed are pending on the steps it sent, or confirmed at once
    /// when it sent none
    /// </summary>
    public static void AfterHandling(CompensatingSaga? saga, MessageContext context)
    {
        if (saga == null) return;
        tieToSteps(saga, saga.PushedNow, context);
    }

    /// <summary>
    /// After an event-sourced saga's handler: as for a document saga, then Wolverine's own events for what the handler
    /// did to the undo stack are appended to the saga's stream
    /// </summary>
    public static void AfterHandlingEventSourced<TSaga>(IEventStream<TSaga> stream, MessageContext context)
        where TSaga : EventSourcedSaga
    {
        var saga = stream.Aggregate;

        // A static Start returns its pushes as events
        var pushed = (saga?.PushedNow ?? []).Concat(stream.Events.Select(x => x.Data).OfType<SagaUndoPushed>()
                .Select(x => x.Entry))
            .Distinct()
            .ToArray();

        tieToSteps(saga, pushed, context);

        if (saga != null)
        {
            foreach (var e in saga.TakeUnsavedEvents()) stream.AppendOne(e);
        }
    }

    private static void tieToSteps(CompensatingSaga? saga, IReadOnlyList<UndoEntry> pushed, MessageContext context)
    {
        var pushedBy = context.Envelope?.Message?.GetType().ToMessageTypeName() ?? "";
        var steps = context.Outstanding.Where(x => isStep(saga, x)).ToArray();

        foreach (var entry in pushed)
        {
            entry.PushedBy = pushedBy;
            entry.Pending = steps.Length > 0 && saga?.ConfirmedNow(entry.Id) != true;
        }

        // What the saga sends carries the ids of the entries its steps confirm, and nothing it inherited
        var ids = string.Join(",", pushed.Select(x => x.Id));
        foreach (var envelope in context.Outstanding)
        {
            if (envelope.Message is null || !envelope.HasHeaders && pushed.Count == 0) continue;

            if (pushed.Count > 0 && steps.Contains(envelope))
            {
                envelope.Headers[StepHeader] = ids;
            }
            else if (envelope.HasHeaders)
            {
                envelope.Headers.Remove(StepHeader);
            }
        }
    }

    // A message the saga sent that is a step: not a timeout, an undo message, or Wolverine's own traffic
    private static bool isStep(CompensatingSaga? saga, Envelope envelope)
    {
        var message = envelope.Message;
        if (message is null or TimeoutMessage or INotToBeRouted) return false;
        if (envelope.HasHeaders && envelope.Headers.ContainsKey(EntryHeader)) return false;
        if (envelope.Status == EnvelopeStatus.Scheduled) return false;

        return saga == null || !saga.IsUndoMessage(message);
    }

    // ---- a sequential unwind ----------------------------------------------------------------------------------------

    /// <summary>
    /// An undo message of a sequential unwind, sent asking for Wolverine's acknowledgement (or the business reply it was
    /// pushed with), and for its fault if it fails for good
    /// </summary>
    internal static object SendForConfirmation(CompensatingSaga saga, UndoEntry entry, object message)
    {
        var options = new DeliveryOptions().ReturnFaultToSender();
        options.Headers[EntryHeader] = $"{saga.GetType().FullName}|{entry.Id}";

        if (entry.ConfirmedByType() is { } reply)
        {
            options.ResponseType = reply;
        }
        else
        {
            options.AckRequested = true;
        }

        return new DeliveryMessage<object>(message, options);
    }

    /// <summary>
    /// A reply that carries a sequential unwind's undo entry -- Wolverine's acknowledgement, the business reply the saga
    /// named, or the undo message's fault -- becomes the <see cref="UndoReply{TSaga}" /> its saga's chain handles
    /// </summary>
    [UnconditionalSuppressMessage("Trimming", "IL2072", Justification = "UndoReply<T> closed over a saga type rooted by handler discovery.")]
    [UnconditionalSuppressMessage("Trimming", "IL2075", Justification = "Fault<T>.Exception, a public member of Wolverine's own Fault<T>.")]
    internal static bool TryConvertToUndoReply(Envelope envelope)
    {
        if (!envelope.HasHeaders || !envelope.Headers.TryGetValue(EntryHeader, out var value) || value == null) return false;
        if (envelope.Message is UndoReply) return false;

        var separator = value.LastIndexOf('|');
        if (separator < 0 || !Guid.TryParse(value[(separator + 1)..], out var entryId)) return false;
        if (!_replyTypes.TryGetValue(value[..separator], out var replyType)) return false;

        // The undo message's fault: from its body when this service can read it, else from its headers
        string? failure = null;
        var messageType = envelope.Message?.GetType();
        if (messageType != null && SagaCompensations.FaultMessageType(messageType) != null &&
            messageType.GetProperty(nameof(Fault<object>.Exception))?.GetValue(envelope.Message) is ExceptionInfo info)
        {
            failure = $"{info.Type}: {info.Message}";
        }
        else if (envelope.Headers.ContainsKey(FaultHeaders.AutoPublished))
        {
            failure = envelope.Headers.TryGetValue(FaultHeaders.OriginalType, out var original)
                ? $"{original} failed"
                : "The undo message failed";
        }

        envelope.Message = Activator.CreateInstance(replyType, entryId, failure == null, failure);
        envelope.MessageType = replyType.ToMessageTypeName();
        return true;
    }

    /// <summary>A sequential unwind heard back about one undo message on a document saga</summary>
    public static async Task ContinueAsync<TSaga>(TSaga saga, UndoReply<TSaga> reply, MessageContext context)
        where TSaga : CompensatingSaga
    {
        var next = continueUnwind(saga, reply, context);
        if (next != null) await context.EnqueueCascadingAsync(next).ConfigureAwait(false);
    }

    /// <summary>A sequential unwind heard back about one undo message on an event-sourced saga</summary>
    public static async Task ContinueEventSourcedAsync<TSaga>(IEventStream<TSaga> stream, UndoReply<TSaga> reply,
        MessageContext context) where TSaga : EventSourcedSaga
    {
        var saga = stream.Aggregate ?? throw new UnknownSagaException(typeof(TSaga), stream.Key ?? (object)stream.Id);

        var next = continueUnwind(saga, reply, context);
        foreach (var e in saga.TakeUnsavedEvents()) stream.AppendOne(e);
        if (next != null) await context.EnqueueCascadingAsync(next).ConfigureAwait(false);
    }

    private static object? continueUnwind(CompensatingSaga saga, UndoReply reply, MessageContext context)
    {
        saga.BeginHandling();
        var entry = saga.FindUndo(reply.UndoEntryId);
        if (entry is not { Undoing: true })
        {
            context.Runtime.Logger.LogDebug(
                "Saga {SagaType} is not waiting on undo entry {UndoEntryId}; the reply is ignored",
                saga.GetType().FullNameInCode(), reply.UndoEntryId);
            return null;
        }

        var unwind = saga.Unwinding;
        var step = unwind?.Step ?? entry.PushedBy;
        var exceptionType = unwind?.ExceptionType ?? "";
        var undone = new List<UndoneStep>(unwind?.Undone ?? []);

        if (!reply.Succeeded)
        {
            saga.Record(new SagaUndoFailed(entry.Id, reply.Failure ?? "The undo message failed"));
            saga.Record(new SagaCompensationFailed(step, exceptionType,
                $"Undoing {entry.Tag} with {entry.MessageType} failed: {reply.Failure}")
            {
                Undone = undone.Count == 0 ? null : undone
            });

            context.Runtime.Logger.LogError(
                "Saga {SagaType} could not undo {Step} with {UndoMessageType}: {Failure}. The unwind stopped, and the rest of the undo stack is still there",
                saga.GetType().FullNameInCode(), entry.Tag, entry.MessageType, reply.Failure);
            return null;
        }

        undone.Add(entry.ToUndone());
        saga.Record(new SagaUndoCompleted(entry.Id));

        var next = saga.NextSequentialUndo(unwind?.IncludePending ?? true);
        if (next != null) return next;

        saga.Record(new SagaCompensated(step, exceptionType) { Undone = undone });
        if (unwind?.CompleteWhenDone == true) saga.CompleteAfterUnwind();

        return null;
    }
}

/// <summary>GH-5673. Where a saga's handler meets its undo stack: before the handler, or after it</summary>
internal class SagaUndoFrame : SyncFrame
{
    private readonly Variable _target;
    private readonly string _method;
    private Variable? _context;

    private SagaUndoFrame(Variable target, string method)
    {
        _target = target;
        _method = method;
        uses.Add(target);
    }

    /// <summary>A step's reply confirms the undo entries pushed before the step</summary>
    public static SagaUndoFrame Before(Variable saga) => new(saga, nameof(SagaUndo.BeforeHandling));

    /// <summary>The undo entries the handler pushed are tied to the steps it sent</summary>
    public static SagaUndoFrame After(Variable saga) => new(saga, nameof(SagaUndo.AfterHandling));

    /// <summary>As <see cref="After" />, appending Wolverine's own events to an event-sourced saga's stream</summary>
    public static SagaUndoFrame AfterEventSourced(Variable stream) => new(stream, nameof(SagaUndo.AfterHandlingEventSourced));

    public override IEnumerable<Variable> FindVariables(IMethodVariables chain)
    {
        _context = chain.FindVariable(typeof(MessageContext));
        yield return _context;
    }

    public override void GenerateCode(GeneratedMethod method, ISourceWriter writer)
    {
        writer.Write($"{typeof(SagaUndo).FullNameInCode()}.{_method}({_target.Usage}, {_context!.Usage});");
        Next?.GenerateCode(method, writer);
    }

    public override void GenerateFSharpCode(GeneratedMethod method, ISourceWriter writer)
    {
        writer.Write($"{typeof(SagaUndo).FSharpName()}.{_method}({_target.FSharpUsage}, {_context!.Usage})");
        Next?.GenerateFSharpCode(method, writer);
    }
}

/// <summary>GH-5673. In a saga's <see cref="UndoReply{TSaga}" /> chain, carry its sequential unwind on</summary>
internal class ContinueUnwindFrame : AsyncFrame
{
    private readonly Variable _target;
    private readonly Type _replyType;
    private readonly bool _eventSourced;
    private Variable? _reply;
    private Variable? _context;

    public ContinueUnwindFrame(Variable sagaOrStream, Type replyType, bool eventSourced)
    {
        _target = sagaOrStream;
        _replyType = replyType;
        _eventSourced = eventSourced;
        uses.Add(sagaOrStream);
    }

    public override IEnumerable<Variable> FindVariables(IMethodVariables chain)
    {
        _reply = chain.FindVariable(_replyType);
        yield return _reply;

        _context = chain.FindVariable(typeof(MessageContext));
        yield return _context;
    }

    private string methodName => _eventSourced ? nameof(SagaUndo.ContinueEventSourcedAsync) : nameof(SagaUndo.ContinueAsync);

    public override void GenerateCode(GeneratedMethod method, ISourceWriter writer)
    {
        var sagaType = _replyType.GetGenericArguments()[0];
        writer.WriteComment("Carry the saga's sequential unwind on");
        writer.Write(
            $"await {typeof(SagaUndo).FullNameInCode()}.{methodName}<{sagaType.FullNameInCode()}>({_target.Usage}, {_reply!.Usage}, {_context!.Usage}).ConfigureAwait(false);");
        Next?.GenerateCode(method, writer);
    }

    public override void GenerateFSharpCode(GeneratedMethod method, ISourceWriter writer)
    {
        var sagaType = _replyType.GetGenericArguments()[0];
        writer.WriteLine("// Carry the saga's sequential unwind on");
        writer.WriteLine(
            $"do! {typeof(SagaUndo).FSharpName()}.{methodName}<{sagaType.FSharpName()}>({_target.FSharpUsage}, {_reply!.FSharpUsage}, {_context!.Usage})");
        Next?.GenerateFSharpCode(method, writer);
    }
}
