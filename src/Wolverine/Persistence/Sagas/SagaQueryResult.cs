using JasperFx.Descriptors;

namespace Wolverine.Persistence.Sagas;

/// <summary>
/// One page of saga instances answered by <see cref="ISagaStoreDiagnostics.QuerySagasAsync"/>.
/// </summary>
/// <param name="Instances">The page's instances, in the requested order (by saga identity when none was requested).</param>
/// <param name="TotalCount">How many instances matched the criteria across all pages.</param>
/// <param name="PageNumber">The 1-based page that was read.</param>
/// <param name="PageSize">The page size actually applied, after clamping to <see cref="SagaQueryCriteria.MaxPageSize"/>.</param>
public record SagaQueryResult(IReadOnlyList<SagaInstanceState> Instances, long TotalCount, int PageNumber, int PageSize)
{
    public static SagaQueryResult Empty(int pageNumber, int pageSize) => new([], 0, pageNumber, pageSize);
}

/// <summary>
/// What a saga storage can honour in <see cref="ISagaStoreDiagnostics.QuerySagasAsync"/> for one saga type,
/// so a UI offers only what works.
/// </summary>
/// <param name="CanQuery">Whether <c>QuerySagasAsync</c> answers at all (paging, ordering by identity and a total count).</param>
/// <param name="CanFilter">Whether <c>DocumentQueryOptions.Where</c> is honoured. When false it is refused, never ignored.</param>
/// <param name="CanOrder">Whether <c>DocumentQueryOptions.OrderBy</c> is honoured. When false it is refused, never ignored.</param>
/// <param name="Reason">Why something is unavailable, for display.</param>
public record SagaQueryCapabilities(bool CanQuery, bool CanFilter, bool CanOrder, string? Reason = null)
{
    /// <summary>A storage that does not implement <c>QuerySagasAsync</c>, or does not own the saga type.</summary>
    public static SagaQueryCapabilities None { get; } =
        new(false, false, false, "This saga storage does not support querying saga instances.");

    /// <summary>Paging and a count, ordered by identity only: the filter and ordering text are refused.</summary>
    public static SagaQueryCapabilities PagingOnly(string reason) => new(true, false, false, reason);

    /// <summary>Everything: filter, ordering, paging and count.</summary>
    public static SagaQueryCapabilities Full { get; } = new(true, true, true);
}
