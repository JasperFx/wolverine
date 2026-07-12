using System;
using System.Diagnostics.CodeAnalysis;
using System.Linq;
using Microsoft.EntityFrameworkCore;

namespace Wolverine.EntityFrameworkCore;

/// <summary>
/// Runtime companion to <see cref="EfCoreLoadProfileExtensions" />: returns the root query for an
/// entity with a named load profile's include graph applied. Generated code appends the by-key
/// <c>FirstOrDefaultAsync(x =&gt; x.Key == id, ct)</c> filter inline, so the key predicate stays
/// statically compiled (trim/AOT friendly) and this helper never builds an expression tree.
/// </summary>
public static class EfCoreLoadProfiles
{
    public static IQueryable<TEntity> QueryFor<
        [DynamicallyAccessedMembers(
            DynamicallyAccessedMemberTypes.PublicConstructors | DynamicallyAccessedMemberTypes.NonPublicConstructors |
            DynamicallyAccessedMemberTypes.PublicFields | DynamicallyAccessedMemberTypes.NonPublicFields |
            DynamicallyAccessedMemberTypes.PublicProperties | DynamicallyAccessedMemberTypes.NonPublicProperties |
            DynamicallyAccessedMemberTypes.Interfaces)] TEntity>(DbContext db, string profile)
        where TEntity : class
    {
        var entityType = db.Model.FindEntityType(typeof(TEntity))
                         ?? throw new InvalidOperationException($"{typeof(TEntity).Name} is not mapped.");

        var profiles = entityType.LoadProfiles();
        if (profiles is null || !profiles.TryGetValue(profile, out var include))
        {
            // Should have been caught at codegen; guard defensively.
            throw new InvalidOperationException(
                $"No '{profile}' load profile is registered for {typeof(TEntity).Name}.");
        }

        return (IQueryable<TEntity>)include(db.Set<TEntity>());
    }
}
