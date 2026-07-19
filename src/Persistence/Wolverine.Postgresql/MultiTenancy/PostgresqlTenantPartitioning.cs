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

    public async Task AddTenantsAsync(ILogger logger, IDatabaseWithTables database,
        IReadOnlyDictionary<string, string?> tenantIdToSuffix, CancellationToken token)
    {
        var values = tenantIdToSuffix.ToDictionary(
            pair => pair.Key,
            pair => pair.Value ?? pair.Key.ToLowerInvariant());

        var statuses =
            await _partitions.AddPartitionToAllTables(logger, (PostgresqlDatabase)database, values, token);

        var failures = statuses.Where(x => x.Status != PartitionMigrationStatus.Complete).ToArray();
        if (failures.Any())
        {
            throw new InvalidOperationException(
                $"Adding tenant partitions failed for table(s) {failures.Select(x => x.Identifier.QualifiedName).Join(", ")}");
        }
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
