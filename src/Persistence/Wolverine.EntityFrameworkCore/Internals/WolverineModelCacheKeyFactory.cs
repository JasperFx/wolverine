using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Wolverine.RDBMS;

namespace Wolverine.EntityFrameworkCore.Internals;

/// <summary>
///     Model cache key factory for Wolverine-integrated DbContexts. EF caches the
///     built model per context type by default, but Wolverine maps its envelope
///     tables into the model using the host's durability schema -- so two hosts in
///     one process using the same DbContext type with different Wolverine schemas
///     must not share a cached model, or the second host silently writes envelopes
///     into the first host's schema (GH-3497)
/// </summary>
public class WolverineModelCacheKeyFactory : IModelCacheKeyFactory
{
    public object Create(DbContext context, bool designTime)
    {
        string? schemaName = null;
        try
        {
            schemaName = context.Database.GetService<DatabaseSettings>()?.SchemaName;
        }
        catch (InvalidOperationException)
        {
            // No Wolverine database settings in play -- fall back to type-only caching
        }

        return (context.GetType(), schemaName, designTime);
    }
}
