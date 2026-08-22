using System.Data.Common;
using JasperFx;
using JasperFx.MultiTenancy;
using Weasel.Core;
using Wolverine.Persistence.Durability;

namespace Wolverine.RDBMS;

public class DatabaseSettings
{
    public DbDataSource? DataSource { get; set; }

    private string? _schemaName;

    public string? ConnectionString { get; set; }

    /// <summary>
    /// The database schema holding Wolverine's tables. This has to be a single database identifier;
    /// a multi-part name is rejected here rather than at the point where it produces unusable DDL.
    /// See <see cref="SchemaNameValidation"/> and GH-3997.
    /// </summary>
    public string? SchemaName
    {
        get => _schemaName;
        set
        {
            SchemaNameValidation.AssertValid(value, nameof(SchemaName));
            _schemaName = value;
        }
    }

    /// <summary>
    /// Returns the schema name properly quoted for use in SQL statements.
    /// Uses ANSI SQL double quotes which work for PostgreSQL and SQL Server (with QUOTED_IDENTIFIER ON).
    /// </summary>
    public string QuotedSchemaName
    {
        get
        {
            if (string.IsNullOrEmpty(SchemaName)) return SchemaName ?? string.Empty;
            // Escape any internal double quotes by doubling them
            var escaped = SchemaName.Replace("\"", "\"\"");
            return $"\"{escaped}\"";
        }
    }

    /// <summary>
    /// True for engines that have no user-defined schemas at all — SQLite, where the only "schema"
    /// names a connection knows are <c>main</c>, <c>temp</c>, and whatever has been ATTACHed. For
    /// those, <see cref="SchemaName"/> is folded into the table names as a prefix instead of being
    /// emitted as a <c>schema.table</c> qualifier, which is what lets several logically separate
    /// Wolverine table sets share one database file. See GH-3943.
    /// </summary>
    public bool SchemaNameIsTablePrefix { get; set; }

    /// <summary>
    /// Renders the storage identifier for one of Wolverine's envelope storage tables, honoring
    /// <see cref="SchemaNameIsTablePrefix"/>. Every reference to a Wolverine table in generated SQL
    /// should go through this rather than interpolating <c>{SchemaName}.{table}</c> directly.
    /// </summary>
    public string TableNameFor(string tableName)
    {
        if (SchemaNameIsTablePrefix) return TablePrefixing.Apply(SchemaName, tableName);

        // No schema configured at all means no qualifier — every dialect then resolves the table
        // against the connection's default schema.
        return string.IsNullOrEmpty(SchemaName) ? tableName : $"{SchemaName}.{tableName}";
    }

    /// <summary>
    /// The <see cref="TableNameFor"/> rendering, but with the schema name quoted for engines that
    /// need it. Prefixed names are single identifiers and are never quoted here.
    /// </summary>
    public string QuotedTableNameFor(string tableName)
    {
        if (SchemaNameIsTablePrefix) return TablePrefixing.Apply(SchemaName, tableName);

        return string.IsNullOrEmpty(SchemaName) ? tableName : $"{QuotedSchemaName}.{tableName}";
    }
    public AutoCreate AutoCreate { get; set; } = JasperFx.AutoCreate.CreateOrUpdate;

    /// <summary>
    ///     Is this database the master database for node storage and any kind of command queueing?
    /// </summary>
    public MessageStoreRole Role { get; set; } = MessageStoreRole.Ancillary;
    
    /// <summary>
    /// If the main database, add a tenant lookup table
    /// </summary>
    public bool AddTenantLookupTable { get; set; } = false;

    /// <summary>
    ///     Is this database exposing command queues?
    /// </summary>
    public bool CommandQueuesEnabled { get; set; } = true;

    public int ScheduledJobLockId { get; set; } = 20000;

    /// <summary>
    /// Advisory lock identifier used to serialize Wolverine schema migrations across
    /// concurrent processes. Prevents race conditions like duplicate CREATE SCHEMA
    /// failures when many test hosts or service instances boot at once.
    /// Defaults to 4006. Set this to Marten's <c>StoreOptions.ApplyChangesLockId</c>
    /// (default 4004) when using <c>IntegrateWithWolverine</c> if you want both
    /// frameworks' migrations to serialize against the same lock.
    /// </summary>
    public int MigrationLockId { get; set; } = 4006;
    
    /// <summary>
    /// Default databases by tenant and connection string to use for seeding
    /// "master table tenancy"
    /// </summary>
    public ITenantedSource<string>? TenantConnections { get; set; }
}
