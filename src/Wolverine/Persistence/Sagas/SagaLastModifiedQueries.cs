using System.Linq.Expressions;
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

/// <summary>
/// GH-5572. The <see cref="Saga.CompletedAt" /> filters shared by the LINQ-backed saga storages (document stores, EF
/// Core, RavenDB), for saga types whose completed instances are retained.
/// </summary>
public static class SagaCompletionQueries
{
    /// <summary>
    /// <paramref name="source" /> without the retained, completed sagas, or unchanged when
    /// <paramref name="includeCompleted" /> is true or <typeparamref name="T" /> is not a saga.
    /// </summary>
    public static IQueryable<T> OpenOnly<T>(IQueryable<T> source, bool includeCompleted = false)
    {
        if (includeCompleted || !typeof(Saga).IsAssignableFrom(typeof(T))) return source;

        var parameter = Expression.Parameter(typeof(T), "x");
        var completedAt = Expression.Property(parameter, typeof(Saga).GetProperty(nameof(Saga.CompletedAt))!);
        var isOpen = Expression.Equal(completedAt, Expression.Constant(null, typeof(DateTimeOffset?)));
        return source.Where(Expression.Lambda<Func<T, bool>>(isOpen, parameter));
    }

    /// <summary>
    /// <paramref name="source" /> restricted to the sagas that completed before <paramref name="completedBefore" />,
    /// the ones a retention sweep deletes
    /// </summary>
    public static IQueryable<TSaga> CompletedBefore<TSaga>(IQueryable<TSaga> source, DateTimeOffset completedBefore)
        where TSaga : Saga
    {
        return source.Where(x => x.CompletedAt != null && x.CompletedAt < completedBefore);
    }
}
