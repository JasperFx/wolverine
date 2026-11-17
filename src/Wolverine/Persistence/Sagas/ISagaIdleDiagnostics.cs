using JasperFx.Documents;

namespace Wolverine.Persistence.Sagas;

/// <summary>
/// GH-5558. A range over <see cref="Saga.LastModified" />, the time the saga store last wrote a saga.
/// <see cref="ModifiedAfter" /> is inclusive and <see cref="ModifiedBefore" /> exclusive; either may be left
/// open. "Idle for more than two days" is <c>new SagaLastModifiedRange(ModifiedBefore: now - 2 days)</c>.
/// </summary>
public sealed record SagaLastModifiedRange(DateTimeOffset? ModifiedAfter = null, DateTimeOffset? ModifiedBefore = null)
{
    /// <summary>No bound on either side.</summary>
    public static SagaLastModifiedRange Any { get; } = new();

    /// <summary>Sagas last written before <paramref name="cutoff" />.</summary>
    public static SagaLastModifiedRange Before(DateTimeOffset cutoff) => new(ModifiedBefore: cutoff);

    /// <summary>Sagas last written at or after <paramref name="cutoff" />.</summary>
    public static SagaLastModifiedRange After(DateTimeOffset cutoff) => new(ModifiedAfter: cutoff);
}

/// <summary>
/// GH-5558. Opt-in saga diagnostics over <see cref="Saga.LastModified" />, beside
/// <see cref="ISagaStoreDiagnostics" />: count the instances of a saga type that have not been written since a
/// cutoff, and list instances by a modified-before / modified-after range. Monitoring tools (CritterWatch's idle
/// saga counts and alert rule) use it to find sagas that have stalled.
/// </summary>
/// <remarks>
/// <para>
/// A saga storage that can answer implements this interface next to <see cref="ISagaStoreDiagnostics" />; one
/// that cannot simply does not, and the monitoring tool refuses the feature for that storage. The diagnostics on
/// <see cref="Wolverine.Runtime.IWolverineRuntime.SagaStorage" /> always implement it and route each call to the
/// storage that owns the saga type, so check <see cref="SupportsIdleQueries" /> first.
/// </para>
/// <para>
/// Saga types are named the same way as on <see cref="ISagaStoreDiagnostics" />: the full name in code, or the
/// short type name. Completed sagas are either deleted or, for a retained saga type, left out (GH-5572), so every instance counted here is still open.
/// </para>
/// </remarks>
public interface ISagaIdleDiagnostics
{
    /// <summary>
    /// Whether <see cref="CountIdleSagasAsync" /> and <see cref="QuerySagasByLastModifiedAsync" /> can be answered
    /// for <paramref name="sagaTypeName" />. False for a saga type no storage owns, and for one whose storage does
    /// not implement this interface.
    /// </summary>
    bool SupportsIdleQueries(string sagaTypeName);

    /// <summary>
    /// The number of <paramref name="sagaTypeName" /> instances last written before
    /// <paramref name="modifiedBefore" />. Zero for a saga type no storage owns.
    /// </summary>
    /// <param name="sagaTypeName">The saga type's full name in code, or its short name.</param>
    /// <param name="modifiedBefore">The cutoff, exclusive.</param>
    /// <param name="tenantId">
    /// The tenant whose sagas to count, or null for the default tenant. A storage that cannot scope the count to
    /// a tenant throws <see cref="DocumentCriteriaNotSupportedException" /> rather than count another tenant's sagas.
    /// </param>
    /// <param name="ct">Token to cancel the call.</param>
    /// <exception cref="NotSupportedException">The storage that owns the saga type does not support idle queries.</exception>
    Task<long> CountIdleSagasAsync(string sagaTypeName, DateTimeOffset modifiedBefore, string? tenantId,
        CancellationToken ct);

    /// <summary>
    /// A page of <paramref name="sagaTypeName" /> instances whose <see cref="Saga.LastModified" /> falls in
    /// <paramref name="range" />, with the total count, least recently modified first unless
    /// <see cref="DocumentQueryOptions.OrderBy" /> says otherwise. Paging, <see cref="DocumentQueryOptions.Where" />
    /// and <see cref="DocumentQueryOptions.OrderBy" /> follow the same rules as
    /// <see cref="ISagaStoreDiagnostics.QuerySagasAsync(string, DocumentQueryOptions, CancellationToken)" />; <see cref="DocumentQueryOptions.TenantId" /> names the
    /// tenant, with the same refusal rule as <see cref="CountIdleSagasAsync" />.
    /// </summary>
    /// <exception cref="NotSupportedException">The storage that owns the saga type does not support idle queries.</exception>
    Task<SagaQueryResult> QuerySagasByLastModifiedAsync(string sagaTypeName, SagaLastModifiedRange range,
        DocumentQueryOptions options, CancellationToken ct);
}
