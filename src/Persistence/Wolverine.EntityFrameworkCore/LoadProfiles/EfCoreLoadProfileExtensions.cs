using System;
using System.Collections.Generic;
using System.Linq;
using Microsoft.EntityFrameworkCore.Metadata;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Wolverine.EntityFrameworkCore;

/// <summary>
/// Declares named "load profiles" — include/fetch graphs applied when an aggregate is loaded via
/// <c>[Entity(Profile = "...")]</c>. Profiles live on the EF Core model configuration so they are
/// declared once, next to the mapping, and validated against the model at startup/codegen.
/// </summary>
public static class EfCoreLoadProfileExtensions
{
    internal const string AnnotationKey = "Wolverine:LoadProfiles";

    /// <summary>
    /// Declare a named load profile for this entity. The <paramref name="include"/> transform is applied
    /// to the root <c>DbSet&lt;T&gt;</c> query before the by-key filter, e.g.
    /// <c>q =&gt; q.Include(o =&gt; o.Lines).ThenInclude(l =&gt; l.Product)</c>. Call sites opt in with
    /// <c>[Entity(Profile = "name")]</c>; different handlers on the same aggregate can select different graphs.
    /// </summary>
    public static EntityTypeBuilder<T> HasLoadProfile<T>(
        this EntityTypeBuilder<T> builder, string name, Func<IQueryable<T>, IQueryable<T>> include)
        where T : class
    {
        if (string.IsNullOrWhiteSpace(name))
            throw new ArgumentException("Load profile name must be non-empty.", nameof(name));
        ArgumentNullException.ThrowIfNull(include);

        var map = builder.Metadata.FindAnnotation(AnnotationKey)?.Value as Dictionary<string, Func<IQueryable, IQueryable>>
                  ?? new Dictionary<string, Func<IQueryable, IQueryable>>(StringComparer.Ordinal);

        map[name] = q => include((IQueryable<T>)q);
        builder.Metadata.SetAnnotation(AnnotationKey, map);

        return builder;
    }

    /// <summary>The load profiles declared for this entity type, or null if none.</summary>
    internal static IReadOnlyDictionary<string, Func<IQueryable, IQueryable>>? LoadProfiles(this IReadOnlyEntityType entityType)
        => entityType.FindAnnotation(AnnotationKey)?.Value as Dictionary<string, Func<IQueryable, IQueryable>>;
}
