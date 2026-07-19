using Wolverine.RDBMS.MultiTenancy;

namespace Wolverine.EntityFrameworkCore;

/// <summary>
///     Options for Wolverine's conjoined EF Core multi-tenancy
/// </summary>
public class ConjoinedTenancyOptions
{
    internal TenantPartitioningOptions? Partitioning { get; private set; }

    public bool PartitioningEnabled => Partitioning != null;

    /// <summary>
    ///     Name of the Weasel partition control/registry table created in the
    ///     Wolverine durability schema. Default is wolverine_tenant_partitions
    /// </summary>
    public string PartitionControlTableName { get; set; } = "wolverine_tenant_partitions";

    /// <summary>
    ///     Opt into Weasel-managed physical partitioning: every non-saga ITenanted
    ///     entity table is partitioned per tenant (PostgreSQL LIST partitions on
    ///     tenant_id; SQL Server RANGE partitions over a tenant ordinal column).
    ///     Requires UseEntityFrameworkCoreWolverineManagedMigrations() -- EF
    ///     migrations cannot express the partition DDL
    /// </summary>
    public void PartitionPerTenant(Action<TenantPartitioningOptions>? configure = null)
    {
        Partitioning = new TenantPartitioningOptions();
        configure?.Invoke(Partitioning);
    }
}
