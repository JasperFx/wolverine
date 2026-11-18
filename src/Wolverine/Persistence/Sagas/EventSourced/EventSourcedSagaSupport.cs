using System.ComponentModel;
using System.Runtime.CompilerServices;
using JasperFx.Core.Reflection;
using JasperFx.Events;
using Wolverine.Configuration;
using Wolverine.Persistence.EventSourcing;

namespace Wolverine.Persistence.Sagas;

/// <summary>
/// GH-5415. Called by the generated code of an <see cref="EventSourcedSaga" />'s handlers. Not for application code.
/// </summary>
[EditorBrowsable(EditorBrowsableState.Never)]
public static class EventSourcedSagaSupport
{
    /// <summary>
    /// After a handler ran: a saga the handler completed with <c>MarkCompleted()</c> gets a <see cref="SagaCompleted" />
    /// event, unless the handler returned one itself, and a saga type marked <see cref="ArchiveWhenCompletedAttribute" />
    /// gets an <see cref="Archived" /> event with it.
    /// </summary>
    public static void AfterHandling<TSaga>(IEventStream<TSaga> stream, TSaga? saga, bool archiveWhenCompleted)
        where TSaga : EventSourcedSaga
    {
        var appended = stream.Events;
        var completedByEvent = appended.Any(x => x.Data is SagaCompleted or Archived);

        // A saga loaded completed never reaches a handler, so a completed saga with no completion time was completed
        // by this handler with MarkCompleted()
        var markedCompleted = saga != null && saga.IsCompleted() && saga.CompletedAt == null;

        if (markedCompleted && !completedByEvent)
        {
            stream.AppendOne(new SagaCompleted());
            completedByEvent = true;
        }

        if (archiveWhenCompleted && completedByEvent && !appended.Any(x => x.Data is Archived))
        {
            stream.AppendOne(new Archived(ArchiveWhenCompletedAttribute.Reason));
        }
    }

    /// <summary>
    /// A message that only starts the saga, for a saga that already exists: refused as a duplicate saga start is
    /// refused today, rather than appended to the existing stream
    /// </summary>
    public static Exception StartOfExistingSaga(Type sagaType, object sagaId)
        => new SagaConcurrencyException(
            $"Saga of type {sagaType.FullNameInCode()} and id {sagaId} cannot be started because its event stream already exists.");

    /// <summary>
    /// The store refused to start the saga's stream because another message started it in the meantime: JasperFx.Events'
    /// <see cref="ExistingStreamIdCollisionException" />, which Marten, Polecat and Fisher all throw, or an optimistic
    /// concurrency failure on appending the first events of a stream FetchForWriting found empty
    /// </summary>
    public static bool IsStreamCollision(Exception e)
        => e is ExistingStreamIdCollisionException or JasperFx.ConcurrencyException and not SagaConcurrencyException;

    /// <summary>
    /// The <see cref="SagaConcurrencyException" /> a lost race to start the saga's stream is reported as
    /// </summary>
    public static Exception StreamCollision(Type sagaType, object sagaId, Exception inner)
        => new SagaConcurrencyException(
            $"Saga of type {sagaType.FullNameInCode()} and id {sagaId} cannot be started because another message started its event stream first.",
            inner);

    /// <summary>
    /// GH-5415 with GH-5556. Compensate the failed <typeparamref name="TMessage" /> on an event-sourced saga: append
    /// <see cref="SagaCompensating" />, the events the best matching <c>Compensate</c> method returned and
    /// <see cref="SagaCompensated" /> to the saga's stream, and send the messages it returned. When compensation gave up,
    /// append <see cref="SagaCompensating" /> and <see cref="SagaCompensationFailed" /> instead
    /// </summary>
    public static async Task CompensateAsync<TSaga, TMessage>(IEventStream<TSaga> stream, Fault<TMessage> fault,
        Runtime.MessageContext context) where TSaga : EventSourcedSaga where TMessage : class
    {
        ArgumentNullException.ThrowIfNull(stream);
        ArgumentNullException.ThrowIfNull(fault);
        ArgumentNullException.ThrowIfNull(context);

        var saga = stream.Aggregate ?? throw new UnknownSagaException(typeof(TSaga), stream.Key ?? (object)stream.Id);

        var envelope = context.Envelope;
        var live = envelope?.CompensatingException;
        var thrownType = live?.GetType().FullName ?? fault.Exception.Type;
        var messageType = fault.Message.GetType();
        var step = EventSourcedSagaEventNames.StepName(messageType);

        if (envelope != null && envelope.HasHeaders && envelope.Headers.ContainsKey(SagaCompensations.MarkFailedHeader))
        {
            var reason = envelope.Headers.TryGetValue(SagaCompensations.FailureReasonHeader, out var failure) && failure != null
                ? failure
                : "The compensation failed after its retries";
            stream.AppendOne(new SagaCompensating(step, thrownType));
            stream.AppendOne(new SagaCompensationFailed(step, thrownType, reason));
            return;
        }

        var method = SagaCompensationMethods.For(typeof(TSaga)).Select(messageType, fault.Exception, live);
        if (method == null)
        {
            Microsoft.Extensions.Logging.LoggerExtensions.LogDebug(context.Runtime.Logger,
                "No Compensate method of saga {SagaType} matches the failure of {MessageType} with {ExceptionType}, so nothing was compensated",
                typeof(TSaga).FullNameInCode(), messageType.FullNameInCode(), thrownType);
            return;
        }

        var result = await method.InvokeAsync(saga, fault.Message, fault.Exception, live, context).ConfigureAwait(false);
        var (events, messages) = Split(result);

        stream.AppendOne(new SagaCompensating(step, thrownType));
        if (events.Count != 0) stream.AppendMany(events);
        stream.AppendOne(new SagaCompensated(step, thrownType));

        foreach (var message in messages)
        {
            await context.EnqueueCascadingAsync(message).ConfigureAwait(false);
        }

        // Test seam for "another message appends to the saga's stream while it is being compensated"
        if ((context.Runtime as Runtime.WolverineRuntime)?.CompensationHooks.BeforeSave is { } beforeSave)
        {
            await beforeSave(saga).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Split what a <c>Compensate</c> method returned into events for the saga's stream and messages to send, by the
    /// same rule the saga's handlers follow: a Wolverine return type (<see cref="OutgoingMessages" />, a
    /// <see cref="TimeoutMessage" />, anything <see cref="ISendMyself" />) is sent, everything else is an event
    /// </summary>
    internal static (List<object> Events, List<object> Messages) Split(object? result)
    {
        var events = new List<object>();
        var messages = new List<object>();
        add(result, events, messages);
        return (events, messages);
    }

    private static void add(object? value, List<object> events, List<object> messages)
    {
        switch (value)
        {
            case null:
                return;
            case OutgoingMessages:
                messages.Add(value);
                return;

            // EventsToAppend, and each store's own Events collection (Wolverine.Marten.Events and its twins)
            case IEnumerable<object> carried when value is ICarriesEvents or IWolverineReturnType:
                events.AddRange(carried);
                return;
            case IWolverineReturnType:
                messages.Add(value);
                return;
            case ITuple tuple:
                for (var i = 0; i < tuple.Length; i++) add(tuple[i], events, messages);
                return;
            case string:
                events.Add(value);
                return;
            case IEnumerable<object> many:
                foreach (var item in many) add(item, events, messages);
                return;
            default:
                events.Add(value);
                return;
        }
    }
}
