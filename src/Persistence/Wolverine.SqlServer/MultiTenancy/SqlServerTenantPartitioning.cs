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

    public async Task AddTenantsAsync(ILogger logger, IDatabaseWithTables database,
        IReadOnlyDictionary<string, string?> tenantIdToSuffix, CancellationToken token)
    {
        var db = (IDatabase<SqlConnection>)database;
        await _partitions.InitializeAsync(db, token);

        // Tenants without a suffix each get their own auto-allocated ordinal.
        // Tenants sharing a suffix share one ordinal (a shared physical partition),
        // mirroring the PostgreSQL suffix bucketing
        var standalone = tenantIdToSuffix.Where(x => x.Value == null).Select(x => x.Key).ToArray();
        if (standalone.Any())
        {
            await _partitions.AddPartitionToAllTables(logger, db, standalone, token);
        }

        foreach (var bucket in tenantIdToSuffix.Where(x => x.Value != null).GroupBy(x => x.Value!))
        {
            if (!_options.AllowPartitionSharing && bucket.Count() > 1)
            {
                throw new InvalidOperationException(
                    $"Tenants {bucket.Select(x => x.Key).Join(", ")} share partition suffix '{bucket.Key}', but partition sharing is not enabled. Enable AllowPartitionSharing on the tenant partitioning options.");
            }

            // The bucket's ordinal is whichever member is already registered, or
            // the next free ordinal for a brand new bucket
            var members = bucket.Select(x => x.Key).ToArray();
            var existing = members.Where(m => _partitions.Ordinals.ContainsKey(m))
                .Select(m => _partitions.Ordinals[m]).Distinct().ToArray();

            if (existing.Length > 1)
            {
                throw new InvalidOperationException(
                    $"Tenants {members.Join(", ")} for suffix '{bucket.Key}' are already mapped to different partitions ({string.Join(", ", existing)})");
            }

            var ordinal = existing.Length == 1
                ? existing[0]
                : (_partitions.Ordinals.Values.DefaultIfEmpty(0).Max() + 1);

            var mapping = members.ToDictionary(m => m, _ => ordinal);
            var result = await _partitions.AddPartitionsToAllTables(logger, db, mapping, token);

            var failures = result.Tables.Where(x => x.Status != PartitionMigrationStatus.Complete).ToArray();
            if (failures.Any())
            {
                throw new InvalidOperationException(
                    $"Adding tenant partitions failed for table(s) {failures.Select(x => x.Identifier.QualifiedName).Join(", ")}");
            }
        }
    }

    public Task DropTenantsAsync(ILogger logger, IDatabaseWithTables database, IReadOnlyList<string> tenantIds,
        bool deleteData, CancellationToken token)
    {
        return _partitions.DropPartitionFromAllTables(logger, (IDatabase<SqlConnection>)database, tenantIds,
            deleteData ? TenantDropBehavior.DeleteData : TenantDropBehavior.RetainData, token);
    }
}
