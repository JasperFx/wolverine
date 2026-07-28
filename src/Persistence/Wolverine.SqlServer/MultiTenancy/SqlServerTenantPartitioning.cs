using JasperFx.Core;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Logging;
using Weasel.Core;
using Weasel.Core.Migrations;
using Weasel.SqlServer.Tables;
using Weasel.SqlServer.Tables.Partitioning;
using Wolverine.RDBMS.MultiTenancy;

namespace Wolverine.SqlServer.MultiTenancy;

public class SqlServerTenantPartitioningProviderFactory : ITenantPartitioningProviderFactory
{
    public bool MatchesEfCoreProvider(string efCoreProviderName)
    {
        return efCoreProviderName.Contains("SqlServer", StringComparison.OrdinalIgnoreCase);
    }

    public ITenantPartitioning Create(DbObjectName controlTableName, TenantPartitioningOptions options)
    {
        return new SqlServerTenantPartitioning(controlTableName, options);
    }
}

/// <summary>
///     SQL Server partition-per-tenant: RANGE RIGHT partitioning over a compact
///     tenant ordinal column, managed through Weasel's ManagedTenantPartitions
///     ordinal registry. Application rows carry the ordinal in a column stamped by
///     Wolverine's tenant interceptor
/// </summary>
internal class SqlServerTenantPartitioning : ITenantPartitioning
{
    private readonly TenantPartitioningOptions _options;
    private readonly ManagedTenantPartitions _partitions;

    public SqlServerTenantPartitioning(DbObjectName controlTableName, TenantPartitioningOptions options)
    {
        _options = options;
        _partitions = new ManagedTenantPartitions(controlTableName.Name, controlTableName,
            options.TenantOrdinalColumn)
        {
            AllowOrdinalSharing = options.AllowPartitionSharing
        };
    }

    public bool RequiresTenantOrdinalColumn => true;

    public IReadOnlyList<ISchemaObject> AdditionalSchemaObjects => _partitions.Objects;

    public void ApplyToTable(ITable table)
    {
        var sqlTable = (Table)table;
        sqlTable.PartitionByManagedTenants(_partitions);

        // The clustered primary key must be aligned with the partition scheme,
        // which requires the partition (ordinal) column as a key member. The EF
        // model keeps the user's own single key
        sqlTable.ModifyColumn(_options.TenantOrdinalColumn).AsPrimaryKey();
    }

    public void AttachInitializer(IDatabaseWithTables database)
    {
        ((DatabaseBase<SqlConnection>)database).AddInitializer(_partitions);
    }

    public Task InitializeAsync(IDatabaseWithTables database, CancellationToken token)
    {
        return _partitions.InitializeAsync((IDatabase<SqlConnection>)database, token);
    }

    public bool TryGetOrdinal(string tenantId, out int ordinal)
    {
        return _partitions.Ordinals.TryGetValue(tenantId, out ordinal);
    }

    public async Task<TenantPartitionResult> AddTenantsAsync(ILogger logger, IDatabaseWithTables database,
        IReadOnlyDictionary<string, string?> tenantIdToSuffix, CancellationToken token)
    {
        var db = (IDatabase<SqlConnection>)database;
        await _partitions.InitializeAsync(db, token);

        if (!_options.AllowPartitionSharing)
        {
            assertNoSharing(tenantIdToSuffix);
        }

        // Weasel resolves each named bucket to its ordinal through the registry's bucket column, so
        // members of one bucket land in the same physical partition whether they were registered
        // together or one release apart. Wolverine used to resolve the ordinal itself from the
        // tenant -> ordinal map, which could not see the bucket at all: a brand new tenant matched
        // nothing and silently got a fresh partition (GH-3683 / weasel#391)
        var result = await _partitions.AddPartitionsToAllTables(logger, db, tenantIdToSuffix, token);

        return new TenantPartitionResult(
            result.Ordinals.ToDictionary(x => x.Key, x => x.Value),
            result.Tables.Select(toStatus).ToList());
    }

    /// <summary>
    ///     The bucket -> ordinal map is persisted, so this check spans calls just like the PostgreSQL
    ///     one: two tenants land in the same bucket whether they were registered together or one
    ///     release apart
    /// </summary>
    private void assertNoSharing(IReadOnlyDictionary<string, string?> tenantIdToSuffix)
    {
        foreach (var bucket in tenantIdToSuffix.Where(x => x.Value != null).GroupBy(x => x.Value!))
        {
            var alreadyRegistered = _partitions.Buckets.TryGetValue(bucket.Key, out var ordinal)
                ? _partitions.Ordinals.Where(x => x.Value == ordinal).Select(x => x.Key)
                : [];

            var members = bucket.Select(x => x.Key).Concat(alreadyRegistered).Distinct().ToArray();

            if (members.Length > 1)
            {
                throw new InvalidOperationException(
                    $"Tenants {members.Join(", ")} share partition suffix '{bucket.Key}', but partition sharing is not enabled. Enable AllowPartitionSharing on the tenant partitioning options.");
            }
        }
    }

    public async Task<TenantPartitionResult> MigrateAllTablesAsync(ILogger logger, IDatabaseWithTables database,
        CancellationToken token)
    {
        var db = (IDatabase<SqlConnection>)database;
        var statuses = await _partitions.MigrateAllTablesAsync(logger, db, token);

        return new TenantPartitionResult(
            _partitions.Ordinals.ToDictionary(x => x.Key, x => x.Value),
            statuses.Select(toStatus).ToList());
    }

    private static TenantPartitionTableStatus toStatus(TablePartitionStatus status)
    {
        return new TenantPartitionTableStatus(status.Identifier.QualifiedName, status.Status switch
        {
            PartitionMigrationStatus.Complete => TenantPartitionStatus.Complete,
            PartitionMigrationStatus.RequiresTableRebuild => TenantPartitionStatus.RequiresTableRebuild,
            _ => TenantPartitionStatus.Failed
        });
    }

    public Task DropTenantsAsync(ILogger logger, IDatabaseWithTables database, IReadOnlyList<string> tenantIds,
        bool deleteData, CancellationToken token)
    {
        return _partitions.DropPartitionFromAllTables(logger, (IDatabase<SqlConnection>)database, tenantIds,
            deleteData ? TenantDropBehavior.DeleteData : TenantDropBehavior.RetainData, token);
    }
}
