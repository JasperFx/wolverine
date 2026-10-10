using JasperFx.Core;
using Microsoft.Extensions.Logging;
using Npgsql;
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
internal class PostgresqlTenantPartitioning : ITenantPartitioning, ITenantPartitionRebuilder
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

    // ---- GH-3541: enabling partitioning over an existing table -------------------------------

    private IEnumerable<Table> managedTables(IDatabaseWithTables database)
    {
        return database.AllObjects().OfType<Table>()
            .Where(x => x.Partitioning is ListPartitioning list && list.PartitionManager == _partitions);
    }

    public async Task<IReadOnlyList<UnpartitionedTable>> FindUnpartitionedTablesAsync(IDatabaseWithTables database,
        CancellationToken token)
    {
        var list = new List<UnpartitionedTable>();

        await using var conn = ((PostgresqlDatabase)database).CreateConnection();
        await conn.OpenAsync(token);

        foreach (var table in managedTables(database))
        {
            // relkind 'p' is a partitioned parent; 'r' is a plain table; no row means no table yet
            await using var relkind = conn.CreateCommand(
                "select c.relkind from pg_class c join pg_namespace n on n.oid = c.relnamespace where n.nspname = :schema and c.relname = :name");
            relkind.Parameters.AddWithValue("schema", table.Identifier.Schema);
            relkind.Parameters.AddWithValue("name", table.Identifier.Name);

            var kind = await relkind.ExecuteScalarAsync(token);
            if (kind is not char c || c == 'p')
            {
                continue;
            }

            list.Add(await describeAsync(conn, table, token));
        }

        return list;
    }

    private async Task<UnpartitionedTable> describeAsync(NpgsqlConnection conn, Table table, CancellationToken token)
    {
        var qualified = PostgresqlObjectName.From(table.Identifier);
        var tenantColumn = SchemaUtils.QuoteName(_options.TenantIdColumn);

        var tenants = new List<string>();
        long rows = 0;

        await using var cmd = conn.CreateCommand(
            $"select {tenantColumn}, count(*) from {qualified} group by {tenantColumn}");
        await using var reader = await cmd.ExecuteReaderAsync(token);
        while (await reader.ReadAsync(token))
        {
            if (!await reader.IsDBNullAsync(0, token))
            {
                tenants.Add(reader.GetString(0));
            }

            rows += reader.GetInt64(1);
        }

        return new UnpartitionedTable(table.Identifier, rows, tenants);
    }

    public string WriteRebuildScript(IDatabaseWithTables database, UnpartitionedTable unpartitioned)
    {
        var db = (PostgresqlDatabase)database;
        var table = managedTables(database).Single(x => x.Identifier.QualifiedName == unpartitioned.Identifier.QualifiedName);

        var original = PostgresqlObjectName.From(table.Identifier);
        var backup = PostgresqlObjectName.From(backupNameFor(table.Identifier));

        // The rebuilt table has exactly the columns the plain one had -- the partition column is
        // the tenant id column every conjoined table already carries -- so the reload is a straight
        // column-for-column copy. Quoted, because EF-mapped column names are not always lower case
        var columns = table.Columns.Select(x => x.QuotedName).Join(", ");

        var writer = new StringWriter();
        writer.WriteLine($"-- GH-3541: rebuild {table.Identifier.QualifiedName} as a partitioned table, keeping its {unpartitioned.RowCount} rows");
        writer.WriteLine($"create table {backup} as select * from {original};");
        writer.WriteLine($"drop table {original} cascade;");
        writer.WriteLine();

        // The same definition that creates the table on a fresh database: the partitioned parent,
        // one partition per registered tenant, then the indexes and foreign keys
        table.WriteCreateStatement(db.Migrator, writer);

        writer.WriteLine();
        writer.WriteLine($"insert into {original} ({columns}) select {columns} from {backup};");
        writer.WriteLine();
        writer.WriteLine("-- Every row must have found a partition; a tenant without one fails the insert above, but belt and braces");
        writer.WriteLine("do $$ begin");
        writer.WriteLine($"  if (select count(*) from {original}) <> (select count(*) from {backup}) then");
        writer.WriteLine($"    raise exception 'Row count mismatch after rebuilding {table.Identifier.QualifiedName} as partitioned; the original rows are still in {backup.QualifiedName}';");
        writer.WriteLine("  end if;");
        writer.WriteLine("end $$;");
        writer.WriteLine($"drop table {backup} cascade;");

        return writer.ToString();
    }

    public async Task RebuildAsync(ILogger logger, IDatabaseWithTables database, UnpartitionedTable table,
        CancellationToken token)
    {
        var script = WriteRebuildScript(database, table);

        await using var conn = ((PostgresqlDatabase)database).CreateConnection();
        await conn.OpenAsync(token);
        await using var tx = await conn.BeginTransactionAsync(token);

        await using var cmd = conn.CreateCommand(script);
        cmd.Transaction = tx;
        await cmd.ExecuteNonQueryAsync(token);

        await tx.CommitAsync(token);

        logger.LogInformation("Rebuilt {Table} as a tenant-partitioned table, keeping {Rows} rows across tenants {Tenants}",
            table.Identifier.QualifiedName, table.RowCount, table.TenantIds.Join(", "));
    }

    public async Task DropEmptyTableAsync(IDatabaseWithTables database, UnpartitionedTable table,
        CancellationToken token)
    {
        if (table.RowCount != 0)
        {
            throw new InvalidOperationException(
                $"{table.Identifier.QualifiedName} holds {table.RowCount} rows and cannot be dropped; rebuild it instead");
        }

        await using var conn = ((PostgresqlDatabase)database).CreateConnection();
        await conn.OpenAsync(token);
        await using var cmd = conn.CreateCommand($"drop table {PostgresqlObjectName.From(table.Identifier)} cascade;");
        await cmd.ExecuteNonQueryAsync(token);
    }

    private static DbObjectName backupNameFor(DbObjectName identifier)
    {
        return new DbObjectName(identifier.Schema, identifier.Name + "_unpartitioned");
    }
}
