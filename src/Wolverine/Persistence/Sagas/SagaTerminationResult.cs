using JasperFx.Core.Reflection;

namespace Wolverine.Persistence.Sagas;

/// <summary>
/// GH-5839. What <see cref="ISagaStoreDiagnostics.CompleteSagaAsync" /> did to one saga instance.
/// </summary>
public enum SagaTerminationOutcome
{
    /// <summary>The saga was deleted, as <c>MarkCompleted()</c> deletes a saga whose type is not retained.</summary>
    Deleted,

    /// <summary>
    /// The saga was kept and marked completed (<see cref="Saga.CompletedAt" />), as <c>MarkCompleted()</c> does for a
    /// retained saga type (<see cref="RetainWhenCompletedAttribute" /> or <c>opts.Policies.RetainCompletedSagas()</c>).
    /// </summary>
    Retained,

    /// <summary>
    /// A <see cref="SagaCompleted" /> event, carrying the reason, was appended to an event-sourced saga's stream. Nothing
    /// was deleted.
    /// </summary>
    CompletionAppended,

    /// <summary>The saga had already completed, so nothing was changed.</summary>
    AlreadyCompleted,

    /// <summary>No saga of that type has that identity, so nothing was changed.</summary>
    NotFound
}

/// <summary>
/// GH-5839. The answer to <see cref="ISagaStoreDiagnostics.CompleteSagaAsync" />.
/// </summary>
/// <param name="SagaTypeName">The saga type's full name in code.</param>
/// <param name="Identity">The saga identity as the caller gave it.</param>
/// <param name="Outcome">What was done.</param>
public sealed record SagaTerminationResult(string SagaTypeName, object Identity, SagaTerminationOutcome Outcome)
{
    /// <summary>Whether this call ended the saga: it was deleted, retained as completed, or had its completion appended.</summary>
    public bool Terminated => Outcome is SagaTerminationOutcome.Deleted or SagaTerminationOutcome.Retained
        or SagaTerminationOutcome.CompletionAppended;

    internal static SagaTerminationResult For(Type sagaType, object identity, SagaTerminationOutcome outcome)
        => new(sagaType.FullNameInCode(), identity, outcome);
}

/// <summary>
/// GH-5839. Helpers the saga storages share to complete a saga from outside its handlers, so each one completes it as
/// <c>MarkCompleted()</c> would.
/// </summary>
public static class SagaTermination
{
    /// <summary>
    /// Whether completed instances of <paramref name="sagaType" /> are kept with <see cref="Saga.CompletedAt" /> instead
    /// of being deleted (GH-5572).
    /// </summary>
    public static bool IsRetained(Type sagaType, WolverineOptions options) => SagaRetention.For(sagaType, options) != null;

    /// <summary>
    /// Marks a saga of a retained type completed, now, and stamps <see cref="Saga.LastModified" /> as the saga storages
    /// do on every save.
    /// </summary>
    public static void MarkRetainedCompleted(Saga saga)
    {
        SagaRetention.MarkCompleted(saga);
        saga.LastModified = DateTimeOffset.UtcNow;
    }

    /// <summary>
    /// The refusal for a saga type that <paramref name="storage" /> does not keep, or cannot complete.
    /// </summary>
    public static NotSupportedException NotSupported(string sagaTypeName, string storage)
        => new($"{storage} cannot complete sagas of type {sagaTypeName}: it does not keep that saga type.");

    /// <summary>
    /// The refusal for a saga that changed between being read and being completed.
    /// </summary>
    public static SagaConcurrencyException Concurrency(Type sagaType, object identity, Exception? inner = null)
    {
        var message =
            $"Saga {sagaType.FullNameInCode()} with identity {identity} changed while it was being completed. Read it again and retry.";
        return inner == null ? new SagaConcurrencyException(message) : new SagaConcurrencyException(message, inner);
    }
}
