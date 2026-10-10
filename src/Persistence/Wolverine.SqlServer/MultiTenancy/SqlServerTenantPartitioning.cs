using JasperFx.Core;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Logging;
using Weasel.Core;
using Weasel.Core.Migrations;
using Weasel.SqlServer;
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
internal class SqlServerTenantPartitioning : ITenantPartitioning, ITenantPartitionRebuilder
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

    // ---- GH-3541: enabling partitioning over an existing table -------------------------------

    private IEnumerable<Table> managedTables(IDatabaseWithTables database)
    {
        return database.AllObjects().OfType<Table>()
            .Where(x => ReferenceEquals(x.SqlServerPartitioning, _partitions));
    }

    public async Task<IReadOnlyList<UnpartitionedTable>> FindUnpartitionedTablesAsync(IDatabaseWithTables database,
        CancellationToken token)
    {
        var list = new List<UnpartitionedTable>();

        await using var conn = ((IDatabase<SqlConnection>)database).CreateConnection();
        await conn.OpenAsync(token);

        foreach (var table in managedTables(database))
        {
            // A table is partitioned when its heap or clustered index sits on a partition scheme.
            // OBJECT_ID is null when the table is not there yet, which is the fresh-database case
            await using var probe = conn.CreateCommand();
            probe.CommandText = @"
select case
    when object_id(@name) is null then -1
    when exists (select 1 from sys.indexes i
                     join sys.partition_schemes ps on ps.data_space_id = i.data_space_id
                 where i.object_id = object_id(@name) and i.index_id in (0, 1)) then 1
    else 0 end";
            probe.Parameters.AddWithValue("@name", table.Identifier.QualifiedName);

            var state = (int)(await probe.ExecuteScalarAsync(token))!;
            if (state != 0)
            {
                continue;
            }

            list.Add(await describeAsync(conn, table, token));
        }

        return list;
    }

    private async Task<UnpartitionedTable> describeAsync(SqlConnection conn, Table table, CancellationToken token)
    {
        var qualified = SqlServerObjectName.From(table.Identifier);
        var tenantColumn = SchemaUtils.QuoteName(_options.TenantIdColumn);

        var tenants = new List<string>();
        long rows = 0;

        await using var cmd = conn.CreateCommand();
        cmd.CommandText = $"select {tenantColumn}, count_big(*) from {qualified} group by {tenantColumn}";
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
        var db = (DatabaseBase<SqlConnection>)database;
        var table = managedTables(database).Single(x => x.Identifier.QualifiedName == unpartitioned.Identifier.QualifiedName);

        var original = SqlServerObjectName.From(table.Identifier);
        var backup = SqlServerObjectName.From(backupNameFor(table.Identifier));
        var registry = SqlServerObjectName.From(_partitions.RegistryTableIdentifier);

        // The rebuilt table gains the tenant ordinal column, which the plain table never had: it is
        // filled from the ordinal registry on the way back in, joined on the tenant id. Every other
        // column copies straight across. Quoted, because EF-mapped column names are not always lower case
        var ordinalColumn = SchemaUtils.QuoteName(_options.TenantOrdinalColumn);
        var tenantColumn = SchemaUtils.QuoteName(_options.TenantIdColumn);
        var copied = table.Columns
            .Where(x => !x.Name.Equals(_options.TenantOrdinalColumn, StringComparison.OrdinalIgnoreCase))
            .Select(x => x.QuotedName)
            .ToArray();

        var targetColumns = copied.Append(ordinalColumn).Join(", ");
        var sourceColumns = copied.Select(x => "b." + x).Append("r.[ordinal]").Join(", ");

        var writer = new StringWriter();
        writer.WriteLine($"-- GH-3541: rebuild {table.Identifier.QualifiedName} as a partitioned table, keeping its {unpartitioned.RowCount} rows");
        writer.WriteLine($"select * into {backup} from {original};");
        writer.WriteLine($"drop table {original};");
        writer.WriteLine();

        // The same definition that creates the table on a fresh database: the partition function
        // and scheme over the registered ordinals, the table on that scheme, then its indexes
        table.WriteCreateStatement(db.Migrator, writer);

        writer.WriteLine();
        writer.WriteLine($"insert into {original} ({targetColumns})");
        writer.WriteLine($"  select {sourceColumns} from {backup} b");
        writer.WriteLine($"  join {registry} r on r.[tenant_id] = b.{tenantColumn};");
        writer.WriteLine();
        writer.WriteLine("-- An inner join silently drops rows whose tenant has no ordinal; refuse rather than lose them");
        writer.WriteLine($"if (select count_big(*) from {original}) <> (select count_big(*) from {backup})");
        writer.WriteLine($"  throw 50000, 'Row count mismatch after rebuilding {table.Identifier.QualifiedName} as partitioned; the original rows are still in {backup.QualifiedName}', 1;");
        writer.WriteLine($"drop table {backup};");

        return writer.ToString();
    }

    public async Task RebuildAsync(ILogger logger, IDatabaseWithTables database, UnpartitionedTable table,
        CancellationToken token)
    {
        var script = WriteRebuildScript(database, table);

        await using var conn = ((IDatabase<SqlConnection>)database).CreateConnection();
        await conn.OpenAsync(token);
        await using var tx = (SqlTransaction)await conn.BeginTransactionAsync(token);

        await using var cmd = conn.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = script;
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

        await using var conn = ((IDatabase<SqlConnection>)database).CreateConnection();
        await conn.OpenAsync(token);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = $"drop table {SqlServerObjectName.From(table.Identifier)};";
        await cmd.ExecuteNonQueryAsync(token);
    }

    private static DbObjectName backupNameFor(DbObjectName identifier)
    {
        return new DbObjectName(identifier.Schema, identifier.Name + "_unpartitioned");
    }
}
