namespace Wolverine.RDBMS.MultiTenancy;

/// <summary>
///     Per-table outcome of a tenant partition add or back-fill operation.
///     Weasel.Postgresql and Weasel.SqlServer each declare their own engine-specific
///     status enum; this is the engine-neutral projection Wolverine hands to callers
/// </summary>
public enum TenantPartitionStatus
{
    /// <summary>
    ///     The table's partitions are in sync with the registered tenant set
    /// </summary>
    Complete,

    /// <summary>
    ///     The partition DDL failed for this table. The operation is failure-isolated
    ///     per table, so other tables in the same batch may well have succeeded
    /// </summary>
    Failed,

    /// <summary>
    ///     The table cannot be reconciled additively and needs a full rebuild --
    ///     typically an existing non-partitioned table, or one whose live partition
    ///     set has drifted from the registry
    /// </summary>
    RequiresTableRebuild
}

/// <summary>
///     The outcome of a tenant partition operation for one physical table
/// </summary>
/// <param name="TableName">Qualified name of the partitioned table</param>
/// <param name="Status">What happened to this table</param>
public record TenantPartitionTableStatus(string TableName, TenantPartitionStatus Status);

/// <summary>
///     The result of adding tenants to -- or back-filling -- the Weasel-managed
///     partition set. Partition DDL is applied per table with failures isolated, so
///     a batch can partially succeed; inspect <see cref="Failures" /> rather than
///     assuming an exception-free call touched every table
/// </summary>
/// <param name="Ordinals">
///     Tenant id to partition ordinal, for engines that partition over a compact
///     integer ordinal (SQL Server). Empty on PostgreSQL, which partitions by list
///     on the tenant id itself
/// </param>
/// <param name="Tables">Per-table outcome, one entry per partitioned table</param>
public record TenantPartitionResult(
    IReadOnlyDictionary<string, int> Ordinals,
    IReadOnlyList<TenantPartitionTableStatus> Tables)
{
    public static TenantPartitionResult Empty { get; } =
        new(new Dictionary<string, int>(), []);

    /// <summary>
    ///     Every partitioned table was reconciled successfully
    /// </summary>
    public bool Succeeded => Tables.All(x => x.Status == TenantPartitionStatus.Complete);

    /// <summary>
    ///     The tables that did not reconcile. Re-running the add, or
    ///     MigrateTenantPartitionsAsync, retries them
    /// </summary>
    public IReadOnlyList<TenantPartitionTableStatus> Failures =>
        Tables.Where(x => x.Status != TenantPartitionStatus.Complete).ToList();
}

/// <summary>
///     Thrown when tenant partition DDL failed for at least one table on a code path
///     that cannot hand the caller a <see cref="TenantPartitionResult" /> -- most
///     notably tenant onboarding through IDynamicTenantSource, where silently
///     registering a tenant whose partitions were never created would fail later at
///     the tenant's first write
/// </summary>
public class TenantPartitionException : Exception
{
    public TenantPartitionException(IReadOnlyList<TenantPartitionTableStatus> failures)
        : base(
            $"Tenant partition DDL failed for table(s) {string.Join(", ", failures.Select(x => $"{x.TableName} ({x.Status})"))}")
    {
        Failures = failures;
    }

    /// <summary>
    ///     The tables that did not reconcile
    /// </summary>
    public IReadOnlyList<TenantPartitionTableStatus> Failures { get; }
}
