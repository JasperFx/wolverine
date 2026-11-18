using JasperFx.Events;
using Wolverine.Persistence.Sagas;
using Wolverine.Util;

// ReSharper disable once CheckNamespace
namespace Wolverine;

/// <summary>
/// GH-5415. A saga whose state is an event stream in Marten, Polecat or Fisher instead of a stored document. Its
/// handlers return events (and messages) as an aggregate handler does, and its state is rebuilt from the stream with the
/// stores' own aggregation conventions: <c>Apply</c> / <c>Create</c> / <c>ShouldDelete</c> methods, or one public
/// <c>Evolve(IEvent)</c> / <c>Evolve(object)</c> method.
/// </summary>
/// <remarks>
/// <para>
/// <c>Start</c> returns events and messages, never the saga: the starting state comes only from the events. Wolverine
/// registers every event-sourced saga type as an Inline snapshot of its own stream, in the event store the saga type
/// belongs to.
/// </para>
/// <para>
/// A saga completes when <see cref="SagaCompleted" /> is in its stream: return one among a handler's events, or call
/// <see cref="Saga.MarkCompleted" /> and Wolverine appends it after the handler. Nothing is deleted on completion.
/// <see cref="CompensatingSaga.Status" /> and <see cref="CompensatingSaga.CompensationLog" /> are rebuilt from events
/// Wolverine appends when it compensates a failed step (<see cref="SagaCompensating" />, <see cref="SagaCompensated" />,
/// <see cref="SagaCompensationFailed" />). The <c>Apply</c> methods below handle Wolverine's own events; a saga never
/// handles them itself.
/// </para>
/// </remarks>
public abstract class EventSourcedSaga : CompensatingSaga
{
    /// <summary>Wolverine's own event: the saga completed when this event was recorded.</summary>
    public void Apply(IEvent<SagaCompleted> e)
    {
        CompletedAt ??= e.Timestamp;
    }

    /// <summary>
    /// A stream archived by hand (JasperFx.Events' <see cref="Archived" />) counts as completed at that point too.
    /// </summary>
    public void Apply(IEvent<Archived> e)
    {
        CompletedAt ??= e.Timestamp;
    }

    /// <summary>Wolverine's own event: a compensation began.</summary>
    public void Apply(IEvent<SagaCompensating> e)
    {
        Status = SagaStatus.Compensating;
    }

    /// <summary>Wolverine's own event: a compensation succeeded.</summary>
    public void Apply(IEvent<SagaCompensated> e)
    {
        Status = SagaStatus.Compensated;
        CompensationLog ??= [];
        CompensationLog.Add(new CompensationLogEntry(e.Data.Step, e.Data.ExceptionType, CompensationOutcome.Compensated,
            e.Timestamp));
    }

    /// <summary>
    /// GH-5674. Wolverine's own event: the saga scheduled a timeout that replaces the earlier ones with the same key.
    /// </summary>
    public void Apply(SagaTimeoutScheduled e)
    {
        LatestTimeoutIds ??= new Dictionary<string, Guid>();
        LatestTimeoutIds[e.Key] = e.TimeoutId;
    }

    /// <summary>Wolverine's own event: a compensation failed after its retries.</summary>
    public void Apply(IEvent<SagaCompensationFailed> e)
    {
        Status = SagaStatus.CompensationFailed;
        CompensationLog ??= [];
        CompensationLog.Add(new CompensationLogEntry(e.Data.Step, e.Data.ExceptionType, CompensationOutcome.Failed,
            e.Timestamp));
    }
}

/// <summary>
/// GH-5415. Wolverine's own event: an <see cref="EventSourcedSaga" /> completed at this point in its stream. Return one
/// among a handler's events to complete the saga, or call <c>MarkCompleted()</c> and Wolverine appends it after the
/// handler. Registered with the event store as <c>wolverine_saga_completed</c>.
/// </summary>
public sealed record SagaCompleted;

/// <summary>
/// GH-5415. Archive an <see cref="EventSourcedSaga" />'s stream as soon as it completes: Wolverine appends JasperFx.Events'
/// <see cref="Archived" /> event together with <see cref="SagaCompleted" />.
/// </summary>
/// <remarks>
/// Only for sagas nothing reads asynchronously. The async daemon skips archived events unless a projection or
/// subscription sets <c>IncludeArchivedEvents</c>, so an async projection, a subscription or event forwarding that had
/// not caught up when the saga completed would never see its events. Without this attribute, a completed saga's stream
/// is archived by the retention sweep (<c>RetainCompletedSagas()</c>), long after the daemon has processed it.
/// </remarks>
[AttributeUsage(AttributeTargets.Class)]
public sealed class ArchiveWhenCompletedAttribute : Attribute
{
    /// <summary>The reason recorded on the <see cref="Archived" /> event</summary>
    public const string Reason = "saga completed";
}

internal static class EventSourcedSagaEventNames
{
    // Q1 of the GH-5415 spec review: Wolverine's own event types carry a wolverine_ prefix, so they cannot collide
    // with an application's own SagaCompleted or SagaCompensated event
    public static readonly (Type EventType, string Alias)[] All =
    [
        (typeof(SagaCompleted), "wolverine_saga_completed"),
        (typeof(SagaCompensating), "wolverine_saga_compensating"),
        (typeof(SagaCompensated), "wolverine_saga_compensated"),
        (typeof(SagaCompensationFailed), "wolverine_saga_compensation_failed"),
        (typeof(SagaTimeoutScheduled), "wolverine_saga_timeout_scheduled")
    ];

    public static string StepName(Type messageType) => messageType.ToMessageTypeName();
}
