using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.Extensions.DependencyInjection;
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
        // No Wolverine database settings (no message store) -- type-only caching
        var schemaName = WolverineModelCustomizer.TryResolveDatabaseSettings(context)?.SchemaName;

        return (context.GetType(), schemaName, designTime);
    }
}
