namespace Wolverine.Persistence.Sagas;

/// <summary>
///     GH-5572. A saga storage that can delete retained, completed sagas once they are past their retention period
///     (<c>opts.Policies.RetainCompletedSagas()</c> / <see cref="RetainWhenCompletedAttribute" />). Implemented next to
///     <see cref="ISagaStoreDiagnostics" /> by each storage that supports retention. Wolverine runs one node agent per
///     implementation, so only one node sweeps each store.
/// </summary>
public interface ISagaRetentionSweeper
{
    /// <summary>
    ///     A short, stable name for the store, used in the sweeping agent's identity, e.g. "Marten"
    /// </summary>
    string RetentionStoreName { get; }

    /// <summary>
    ///     Every saga type this storage persists, retained or not
    /// </summary>
    IReadOnlyList<Type> SagaTypesForRetention();

    /// <summary>
    ///     Delete the completed instances of <paramref name="sagaType" /> that completed before
    ///     <paramref name="completedBefore" />. Instances that have not completed are never touched.
    /// </summary>
    /// <returns>The number of sagas deleted, where the store can tell</returns>
    Task<long> DeleteCompletedSagasAsync(Type sagaType, DateTimeOffset completedBefore, CancellationToken ct);
}
