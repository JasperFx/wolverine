using JasperFx;
using Microsoft.Extensions.Logging;
using Weasel.Core;
using Weasel.Core.Migrations;

namespace Wolverine.RDBMS.MultiTenancy;

/// <summary>
///     Options for Weasel-managed, partition-per-tenant physical partitioning of
///     conjoined multi-tenant tables
/// </summary>
public class TenantPartitioningOptions
{
    /// <summary>
    ///     Name of the tenant id discriminator column. Default is tenant_id
    /// </summary>
    public string TenantIdColumn { get; set; } = StorageConstants.TenantIdColumn;

    /// <summary>
    ///     Name of the integer ordinal column used on database engines that can only
    ///     partition by range over a compact value (SQL Server). Default is tenant_ordinal
    /// </summary>
    public string TenantOrdinalColumn { get; set; } = "tenant_ordinal";

    /// <summary>
    ///     Allow multiple tenants to share a single physical partition ("bucketing"),
    ///     via the partition suffix on PostgreSQL or a shared ordinal on SQL Server.
    ///     The answer to "small tenants don't deserve their own partition" and SQL
    ///     Server's partition count ceiling
    /// </summary>
    public bool AllowPartitionSharing { get; set; }
}

/// <summary>
///     Contributed by each database provider package (Wolverine.Postgresql,
///     Wolverine.SqlServer) to supply the Weasel-managed tenant partitioning
///     implementation for that database engine
/// </summary>
public interface ITenantPartitioningProviderFactory
{
    /// <summary>
    ///     Does this factory serve the given EF Core provider (e.g.
    ///     "Npgsql.EntityFrameworkCore.PostgreSQL", "Microsoft.EntityFrameworkCore.SqlServer")?
    /// </summary>
    bool MatchesEfCoreProvider(string efCoreProviderName);

    ITenantPartitioning Create(DbObjectName controlTableName, TenantPartitioningOptions options);
}

/// <summary>
///     Engine-specific Weasel-managed tenant partitioning applied to conjoined
///     multi-tenant tables: one physical partition per tenant (or per shared
///     bucket), driven from a control/registry table in the durability schema
/// </summary>
public interface ITenantPartitioning
{
    /// <summary>
    ///     True when this engine partitions over a compact integer ordinal (SQL
    ///     Server) and therefore needs the tenant ordinal column mapped and
    ///     stamped on every partitioned entity
    /// </summary>
    bool RequiresTenantOrdinalColumn { get; }

    /// <summary>
    ///     Schema objects beyond the entity tables that must be part of the
    ///     migration -- the partition control/registry table
    /// </summary>
    IReadOnlyList<ISchemaObject> AdditionalSchemaObjects { get; }

    /// <summary>
    ///     Attach the partition strategy to a Weasel table mapped from an EF
    ///     entity type
    /// </summary>
    void ApplyToTable(ITable table);

    /// <summary>
    ///     Register this partitioning's database initializer with the Weasel
    ///     database so migrations hydrate the tenant map before computing deltas
    /// </summary>
    void AttachInitializer(IDatabaseWithTables database);

    /// <summary>
    ///     Hydrate (or re-hydrate) the in-memory tenant map from the control table
    /// </summary>
    Task InitializeAsync(IDatabaseWithTables database, CancellationToken token);

    /// <summary>
    ///     Resolve the ordinal for a tenant id on engines where
    ///     RequiresTenantOrdinalColumn is true. Returns false when the tenant has
    ///     no partition yet (or the engine has no ordinal concept)
    /// </summary>
    bool TryGetOrdinal(string tenantId, out int ordinal);

    /// <summary>
    ///     Add partitions for the given tenants across all partitioned tables.
    ///     The dictionary value is the optional partition suffix -- tenants
    ///     sharing a suffix share a physical partition when AllowPartitionSharing
    ///     is enabled; null values give each tenant its own partition
    /// </summary>
    Task AddTenantsAsync(ILogger logger, IDatabaseWithTables database,
        IReadOnlyDictionary<string, string?> tenantIdToSuffix, CancellationToken token);

    /// <summary>
    ///     Remove the given tenants from the partition set. deleteData controls
    ///     whether the tenants' rows are removed where the engine supports
    ///     retaining them
    /// </summary>
    Task DropTenantsAsync(ILogger logger, IDatabaseWithTables database, IReadOnlyList<string> tenantIds,
        bool deleteData, CancellationToken token);
}
