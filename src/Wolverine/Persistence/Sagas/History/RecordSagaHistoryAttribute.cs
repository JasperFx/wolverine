// ReSharper disable once CheckNamespace
namespace Wolverine;

/// <summary>
///     GH-5665. Record a message history for this saga type: one row per message the saga handled, with the message's
///     type and id, when it was handled, the handler, the outcome and the messages it sent, but never the saga's state.
///     The history is read through <c>ISagaHistoryDiagnostics</c>, the same way an event-sourced saga's stream is, so a
///     monitoring tool shows one timeline for both. <c>opts.Policies.RecordSagaHistory()</c> turns it on for every saga.
/// </summary>
/// <remarks>
///     <para>
///     Off by default, because every message the saga handles writes one more row. The rows are kept in the
///     <c>wolverine_saga_history</c> table of the message store, written in the same transaction as the saga where the
///     saga's storage enlists in Wolverine's outbox: the lightweight database saga tables, Marten, Polecat, Fisher and
///     EF Core. Anywhere else the row is written on its own, as a best effort, before the saga's own commit.
///     </para>
///     <para>
///     The history lives exactly as long as the saga: a saga that is deleted when it completes takes its history with
///     it, and a saga kept by <see cref="RetainWhenCompletedAttribute" /> keeps its history until the same sweep
///     deletes both. Failed attempts are not recorded; they are the saga's dead letters. An event-sourced saga needs
///     none of this, because its stream is its history, so the attribute is ignored there.
///     </para>
/// </remarks>
[AttributeUsage(AttributeTargets.Class)]
public class RecordSagaHistoryAttribute : Attribute;
