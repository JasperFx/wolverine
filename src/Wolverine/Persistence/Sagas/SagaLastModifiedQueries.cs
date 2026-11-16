using System.Diagnostics.CodeAnalysis;
using JasperFx.Documents;

namespace Wolverine.Persistence.Sagas;

/// <summary>
/// GH-5558. The <see cref="Saga.LastModified" /> filter and ordering shared by the LINQ-backed implementations of
/// <see cref="ISagaIdleDiagnostics" /> (document stores, EF Core).
/// </summary>
public static class SagaLastModifiedQueries
{
    /// <summary><paramref name="source" /> restricted to <paramref name="range" />.</summary>
    public static IQueryable<TSaga> Filter<TSaga>(IQueryable<TSaga> source, SagaLastModifiedRange range)
        where TSaga : Saga
    {
        if (range.ModifiedAfter is { } after) source = source.Where(x => x.LastModified >= after);
        if (range.ModifiedBefore is { } before) source = source.Where(x => x.LastModified < before);
        return source;
    }

    /// <summary>
    /// <paramref name="source" /> with the options' <c>Where</c> and <c>OrderBy</c> applied, least recently modified
    /// first when no ordering is given. Only the explicit criteria need runtime code generation; a storage refuses
    /// them through its <see cref="SagaQueryCapabilities" /> where that is unavailable.
    /// </summary>
    [UnconditionalSuppressMessage("AOT", "IL3050",
        Justification = "Where/OrderBy text is refused through the storage's SagaQueryCapabilities when dynamic code is unavailable.")]
    [UnconditionalSuppressMessage("Trimming", "IL2026",
        Justification = "Where/OrderBy text is refused through the storage's SagaQueryCapabilities when dynamic code is unavailable.")]
    public static IQueryable<TSaga> Order<TSaga>(IQueryable<TSaga> source, DocumentQueryOptions options,
        string idMember) where TSaga : Saga
    {
        if (string.IsNullOrWhiteSpace(options.Where) && string.IsNullOrWhiteSpace(options.OrderBy))
        {
            return source.OrderBy(x => x.LastModified);
        }

        var ordered = string.IsNullOrWhiteSpace(options.OrderBy)
            ? options with { OrderBy = nameof(Saga.LastModified) }
            : options;
        return SagaQueryCriteria.Apply(ordered, source, idMember);
    }
}
