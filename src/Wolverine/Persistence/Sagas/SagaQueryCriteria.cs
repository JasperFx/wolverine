using System.Diagnostics.CodeAnalysis;
using JasperFx.Documents;

namespace Wolverine.Persistence.Sagas;

/// <summary>
/// Shared rules for <see cref="ISagaStoreDiagnostics.QuerySagasAsync"/> so every saga storage refuses and
/// pages the same way: unsupported criteria throw <see cref="DocumentCriteriaNotSupportedException"/> rather
/// than yielding an unfiltered page, the page size is clamped, and an unordered query is ordered by identity.
/// </summary>
public static class SagaQueryCriteria
{
    /// <summary>The largest page <c>QuerySagasAsync</c> and <c>ListSagaInstancesAsync</c> will return.</summary>
    public const int MaxPageSize = 1000;

    private const string AotMessage = "Saga criteria are translated with runtime code generation.";

    /// <summary>The 1-based page number and clamped page size for <paramref name="options"/>.</summary>
    public static (int pageNumber, int pageSize) Page(DocumentQueryOptions options)
        => (Math.Max(1, options.PageNumber), Math.Clamp(options.PageSize, 1, MaxPageSize));

    /// <summary>
    /// Refuse every criterion that has no meaning for a saga: only <c>Where</c>, <c>OrderBy</c>, <c>Arguments</c>
    /// and paging are honoured, and only by a storage whose capabilities say so.
    /// </summary>
    public static void RefuseUnsupported(DocumentQueryOptions options)
    {
        refuse(options.IdEquals, nameof(DocumentQueryOptions.IdEquals), "read one instance with ReadSagaAsync");
        refuse(options.CorrelationId, nameof(DocumentQueryOptions.CorrelationId));
        refuse(options.CausationId, nameof(DocumentQueryOptions.CausationId));
        refuse(options.LastModifiedBy, nameof(DocumentQueryOptions.LastModifiedBy));
        refuse(options.OrderByMetadata, nameof(DocumentQueryOptions.OrderByMetadata));
        refuse(DocumentQueryOptions.NormalizeTenantId(options.TenantId), nameof(DocumentQueryOptions.TenantId));
        if (options.AllTenants) refuse(true, nameof(DocumentQueryOptions.AllTenants));
    }

    /// <summary>
    /// Refuse a query that carries a <c>Where</c> and/or <c>OrderBy</c> the storage cannot honour.
    /// </summary>
    public static void RefuseCriteria(DocumentQueryOptions options, SagaQueryCapabilities capabilities)
    {
        RefuseUnsupported(options);
        if (!string.IsNullOrWhiteSpace(options.Where) && !capabilities.CanFilter)
            throw new DocumentCriteriaNotSupportedException(nameof(DocumentQueryOptions.Where),
                capabilities.Reason ?? "this saga storage cannot filter on saga members");
        if (!string.IsNullOrWhiteSpace(options.OrderBy) && !capabilities.CanOrder)
            throw new DocumentCriteriaNotSupportedException(nameof(DocumentQueryOptions.OrderBy),
                capabilities.Reason ?? "this saga storage can only order by saga identity");
    }

    /// <summary>
    /// <paramref name="source"/> with the options' filter and ordering applied. With no ordering given the
    /// result is ordered by <paramref name="idMember"/>, and a given ordering is tie-broken by it, so paging
    /// is deterministic. Does not page.
    /// </summary>
    [RequiresDynamicCode(AotMessage)]
    [RequiresUnreferencedCode(AotMessage)]
    public static IQueryable<T> Apply<T>(DocumentQueryOptions options, IQueryable<T> source, string idMember)
    {
        RefuseUnsupported(options);
        var ordered = string.IsNullOrWhiteSpace(options.OrderBy) ? options with { OrderBy = idMember } : options;
        return ordered.ApplyCriteriaTo(source, idMember);
    }

    private static void refuse(object? value, string criterion, string? hint = null)
    {
        if (value is null or false) return;
        throw new DocumentCriteriaNotSupportedException(criterion,
            $"saga queries do not support {criterion}" + (hint is null ? "" : $"; {hint}"));
    }
}
