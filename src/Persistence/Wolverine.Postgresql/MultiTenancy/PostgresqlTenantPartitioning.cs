using JasperFx.Core;
using Microsoft.Extensions.Logging;
using Weasel.Core;
using Weasel.Core.Migrations;
using Weasel.Postgresql;
using Weasel.Postgresql.Tables;
using Weasel.Postgresql.Tables.Partitioning;
using Wolverine.RDBMS.MultiTenancy;

namespace Wolverine.Postgresql.MultiTenancy;

public class PostgresqlTenantPartitioningProviderFactory : ITenantPartitioningProviderFactory
{
    public bool MatchesEfCoreProvider(string efCoreProviderName)
    {
        return efCoreProviderName.Contains("Npgsql", StringComparison.OrdinalIgnoreCase);
    }

    public ITenantPartitioning Create(DbObjectName controlTableName, TenantPartitioningOptions options)
    {
        return new PostgresqlTenantPartitioning(controlTableName, options);
    }
}

/// <summary>
///     PostgreSQL partition-per-tenant: LIST partitioning on the tenant id column,
///     managed through Weasel's ManagedListPartitions control table
/// </summary>
internal class PostgresqlTenantPartitioning : ITenantPartitioning
{
    private readonly ManagedListPartitions _partitions;
    private readonly TenantPartitioningOptions _options;

    public PostgresqlTenantPartitioning(DbObjectName controlTableName, TenantPartitioningOptions options)
    {
        _options = options;
        _partitions = new ManagedListPartitions(controlTableName.Name, controlTableName);
    }

    public bool RequiresTenantOrdinalColumn => false;

    public IReadOnlyList<ISchemaObject> AdditionalSchemaObjects => _partitions.Objects;

    public void ApplyToTable(ITable table)
    {
        var pgTable = (Table)table;
        pgTable.PartitionByList(_options.TenantIdColumn).UsePartitionManager(_partitions);

        // PostgreSQL requires the partition column in every unique constraint, so
        // the DATABASE primary key becomes (id..., tenant_id). The EF model keeps
        // the user's own single key -- ids remain globally unique through Wolverine's
        // tenant stamping, and keyed loads keep their shape
        pgTable.ModifyColumn(_options.TenantIdColumn).AsPrimaryKey();

        // The managed partition set is reconciled additively at add-tenant time;
        // routine migration deltas must not try to rebuild the table around the
        // live partition list
        pgTable.IgnorePartitionsInMigration = true;
    }

    public void AttachInitializer(IDatabaseWithTables database)
    {
        ((PostgresqlDatabase)database).AddInitializer(_partitions);
    }

    public Task InitializeAsync(IDatabaseWithTables database, CancellationToken token)
    {
        return _partitions.InitializeAsync((PostgresqlDatabase)database, token);
    }

    public bool TryGetOrdinal(string tenantId, out int ordinal)
    {
        ordinal = default;
        return false;
    }

    public async Task<TenantPartitionResult> AddTenantsAsync(ILogger logger, IDatabaseWithTables database,
        IReadOnlyDictionary<string, string?> tenantIdToSuffix, CancellationToken token)
    {
        var values = tenantIdToSuffix.ToDictionary(
            pair => pair.Key,
            pair => pair.Value ?? pair.Key.ToLowerInvariant());

        if (!_options.AllowPartitionSharing)
        {
            await _partitions.InitializeAsync((PostgresqlDatabase)database, token);
            assertNoSharing(values);
        }

        var statuses =
            await _partitions.AddPartitionToAllTables(logger, (PostgresqlDatabase)database, values, token);

        return toResult(statuses);
    }

    public async Task<TenantPartitionResult> MigrateAllTablesAsync(ILogger logger, IDatabaseWithTables database,
        CancellationToken token)
    {
        var db = (PostgresqlDatabase)database;
        await _partitions.InitializeAsync(db, token);

        // ManagedListPartitions has no dedicated back-fill entry point, but its add
        // is purely additive and idempotent -- every partition is written as
        // CREATE TABLE IF NOT EXISTS -- so replaying the full registered value ->
        // suffix map reconciles any table that joined the managed set late
        var registered = _partitions.Partitions.ToDictionary(pair => pair.Key, pair => pair.Value);
        if (!registered.Any())
        {
            return TenantPartitionResult.Empty;
        }

        var statuses = await _partitions.AddPartitionToAllTables(logger, db, registered, token);

        return toResult(statuses);
    }

    /// <summary>
    ///     PostgreSQL persists the tenant -> suffix map in the control table, so the
    ///     check spans calls: two tenants land in the same bucket whether they were
    ///     registered together or one release apart
    /// </summary>
    private void assertNoSharing(IReadOnlyDictionary<string, string> values)
    {
        foreach (var bucket in values.GroupBy(x => x.Value))
        {
            var members = bucket.Select(x => x.Key)
                .Concat(_partitions.Partitions.Where(x => x.Value == bucket.Key).Select(x => x.Key))
                .Distinct()
                .ToArray();

            if (members.Length > 1)
            {
                throw new InvalidOperationException(
                    $"Tenants {members.Join(", ")} share partition suffix '{bucket.Key}', but partition sharing is not enabled. Enable AllowPartitionSharing on the tenant partitioning options.");
            }
        }
    }

    private static TenantPartitionResult toResult(IEnumerable<TablePartitionStatus> statuses)
    {
        var tables = statuses.Select(x => new TenantPartitionTableStatus(x.Identifier.QualifiedName,
            x.Status switch
            {
                PartitionMigrationStatus.Complete => TenantPartitionStatus.Complete,
                PartitionMigrationStatus.RequiresTableRebuild => TenantPartitionStatus.RequiresTableRebuild,
                _ => TenantPartitionStatus.Failed
            })).ToList();

        // PostgreSQL partitions by list on the tenant id itself, so there are no ordinals
        return new TenantPartitionResult(new Dictionary<string, int>(), tables);
    }

    public async Task DropTenantsAsync(ILogger logger, IDatabaseWithTables database, IReadOnlyList<string> tenantIds,
        bool deleteData, CancellationToken token)
    {
        // PostgreSQL partition removal is DETACH + DROP -- inherently data-removing.
        // deleteData == false is not supported on this engine
        if (!deleteData)
        {
            throw new NotSupportedException(
                "PostgreSQL tenant partition removal detaches and drops the tenant's partition, which removes its rows. Call with deleteData: true to acknowledge.");
        }

        foreach (var tenantId in tenantIds)
        {
            await _partitions.DropPartitionFromAllTablesForValue((PostgresqlDatabase)database, logger, tenantId,
                token);
        }
    }
}
