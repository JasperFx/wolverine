using JasperFx;
using Wolverine.Logging;

namespace Wolverine.Persistence.Durability;

public interface IMessageStoreAdmin
{
    Task DeleteAllHandledAsync();
    
    /// <summary>
    ///     Clears out all persisted envelopes
    /// </summary>
    /// <returns></returns>
    Task ClearAllAsync();

    /// <summary>
    ///     Rebuilds the envelope storage and clears out any
    ///     persisted state
    /// </summary>
    /// <returns></returns>
    Task RebuildAsync();

    /// <summary>
    ///     Access the current count of persisted envelopes
    /// </summary>
    /// <returns></returns>
    Task<PersistedCounts> FetchCountsAsync();

    /// <summary>
    ///     Fetch all persisted incoming envelopes
    /// </summary>
    /// <returns></returns>
    Task<IReadOnlyList<Envelope>> AllIncomingAsync();

    /// <summary>
    ///     Fetch all persisted, outgoing envelopes
    /// </summary>
    /// <returns></returns>
    Task<IReadOnlyList<Envelope>> AllOutgoingAsync();

    /// <summary>
    ///     Release the node specific ownership of all persisted envelopes so they
    ///     may be claimed by other nodes
    /// </summary>
    /// <returns></returns>
    Task ReleaseAllOwnershipAsync();
    
    /// <summary>
    ///     Release the node specific ownership of all persisted envelopes for a specific node so they
    ///     may be claimed by other nodes
    /// </summary>
    /// <returns></returns>
    Task ReleaseAllOwnershipAsync(int ownerId);

    /// <summary>
    ///     Merely checks whether or not the system can connect to the durable envelope storage as an
    ///     environment check
    /// </summary>
    /// <param name="token"></param>
    /// <returns></returns>
    public Task CheckConnectivityAsync(CancellationToken token);

    /// <summary>
    ///     Verify that the durable envelope storage objects (schema/tables) actually exist and match the
    ///     configured schema, throwing when they do not. Used by <c>resources check</c> so a missing or
    ///     un-provisioned schema is reported as unhealthy rather than passing on connectivity alone.
    ///     The default is a no-op for stores that don't support schema introspection.
    /// </summary>
    Task AssertStorageExistsAsync(CancellationToken token) => Task.CompletedTask;

    /// <summary>
    ///     Verify that the durable envelope storage has been provisioned at all, throwing when it has not.
    ///     Deliberately weaker AND far cheaper than <see cref="AssertStorageExistsAsync" />: it asks only
    ///     whether the storage exists, not whether it matches the configured schema exactly.
    /// </summary>
    /// <remarks>
    ///     This is the startup-path check for <c>AutoBuildMessageStorageOnStartup = AutoCreate.None</c>
    ///     (GH-4166). Startup used to call <see cref="AssertStorageExistsAsync" />, whose RDBMS
    ///     implementation runs the same full Weasel schema diff as the migration itself -- 14 schema
    ///     objects including five stored procedures on SQL Server, measured at ~80ms against a warm local
    ///     database and enough to blow the 30s command timeout on a small Azure SQL tier. That is exactly
    ///     the work <c>None</c> is set to avoid.
    ///
    ///     It is also deliberately tolerant of drift. <c>None</c> is a claim that something else owns the
    ///     schema, so failing startup because a column differs from what this Wolverine version would have
    ///     built punishes the controlled-migration and rolling-deploy cases that set it. Absent storage is
    ///     the failure worth catching early, and it is the one this catches.
    ///
    ///     The default is a no-op for stores that cannot introspect their own storage.
    /// </remarks>
    Task AssertStorageProvisionedAsync(CancellationToken token) => Task.CompletedTask;

    /// <summary>
    ///     Apply any necessary database migrations to bring the underlying envelope
    ///     storage to the configured requirements of the Wolverine system
    /// </summary>
    /// <returns></returns>
    Task MigrateAsync();

    /// <summary>
    ///     Apply any necessary database migrations to bring the underlying envelope storage to the
    ///     configured requirements of the Wolverine system, overriding the store's configured
    ///     AutoCreate rules. This is the path used by explicit setup operations like
    ///     "resources setup" or IHost.SetupResources() where a configured AutoCreate of None
    ///     should not suppress the storage provisioning. The default implementation ignores the
    ///     override and delegates to <see cref="MigrateAsync()"/> for stores whose migrations
    ///     are not gated by AutoCreate rules.
    /// </summary>
    /// <param name="overrideAutoCreate">When not null, use this AutoCreate rule in place of the store's configured value</param>
    Task MigrateAsync(AutoCreate? overrideAutoCreate) => MigrateAsync();
}