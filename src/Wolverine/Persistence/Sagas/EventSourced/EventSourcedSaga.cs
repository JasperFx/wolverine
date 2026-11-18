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
/// <c>Start</c> returns events and messages, never the saga: the starting state comes only from the events. The saga
/// completes by calling <see cref="Saga.MarkCompleted" /> from the <c>Apply</c> (or <c>Evolve</c>) of a terminal event;
/// nothing is deleted on completion. Its <see cref="CompensatingSaga.Status" /> and
/// <see cref="CompensatingSaga.CompensationLog" /> are rebuilt from events Wolverine appends to the saga's stream
/// (<see cref="Persistence.Sagas.SagaCompensating" />, <see cref="Persistence.Sagas.SagaCompensated" />,
/// <see cref="Persistence.Sagas.SagaCompensationFailed" />).
/// </para>
/// <para>
/// Phase 1 of GH-5415 (specifications first): this type exists so the specifications compile. Nothing in Wolverine
/// treats it differently from any other <see cref="CompensatingSaga" /> yet.
/// </para>
/// </remarks>
public abstract class EventSourcedSaga : CompensatingSaga
{
}
