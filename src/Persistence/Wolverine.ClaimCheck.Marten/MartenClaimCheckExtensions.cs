using Marten;
using Microsoft.Extensions.DependencyInjection;
using Wolverine.ClaimCheck.Postgresql;
using Wolverine.Persistence;

namespace Wolverine.ClaimCheck.Marten;

/// <summary>
/// Extension methods for backing Wolverine's claim checks with the application's own Marten database.
/// </summary>
public static class MartenClaimCheckExtensions
{
    /// <summary>
    /// Store off-loaded claim-check payloads in the application's Marten database as <c>bytea</c> rows.
    /// The table is created on first use in Marten's schema, and the store supports Wolverine-driven
    /// payload expiration. See GH-3566.
    /// </summary>
    /// <remarks>
    /// The <see cref="IDocumentStore"/> does not exist until the container is built, so the store is
    /// registered in DI and resolved at startup through Wolverine's deferred claim-check store (GH-3564)
    /// rather than being constructed here. That indirection is transparent: the expiration sweeper unwraps
    /// it before checking for sweep support, so a Marten-backed store is swept like any other.
    /// </remarks>
    /// <param name="config">The claim check configuration to attach to.</param>
    /// <param name="schemaName">
    /// Schema that owns the claim check table. Defaults to Marten's own <c>DatabaseSchemaName</c>.
    /// </param>
    /// <param name="tableName">Name of the claim check table.</param>
    public static ClaimCheckConfiguration UseMartenClaimCheck(
        this ClaimCheckConfiguration config,
        string? schemaName = null,
        string tableName = PostgresqlClaimCheckStore.DefaultTableName)
    {
        ArgumentNullException.ThrowIfNull(config);

        config.Options.Services.AddSingleton<IClaimCheckStore>(s =>
            new MartenClaimCheckStore(s.GetRequiredService<IDocumentStore>(), schemaName, tableName));

        return config;
    }
}
