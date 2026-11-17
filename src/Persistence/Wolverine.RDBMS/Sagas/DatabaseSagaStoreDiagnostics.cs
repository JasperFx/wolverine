using System.Data.Common;
using System.Diagnostics.CodeAnalysis;
using System.Text.Json;
using System.Text.Json.Serialization;
using JasperFx.Core.Reflection;
using JasperFx.Descriptors;
using JasperFx.Documents;
using Microsoft.Extensions.DependencyInjection;
using Wolverine.Configuration.Capabilities;
using Wolverine.Persistence.Durability;
using Wolverine.Persistence.Sagas;
using Wolverine.Runtime;

namespace Wolverine.RDBMS.Sagas;

/// <summary>
/// Lightweight-RDBMS implementation of <see cref="ISagaStoreDiagnostics"/>.
/// Surfaces saga state stored in Wolverine's per-saga-type tables
/// (one row per saga, identified by <c>id</c> with a JSON <c>body</c>
/// column) for monitoring tools. Works against any
/// <see cref="IMessageDatabase"/>-backed store - Postgres, SQL Server,
/// MySQL, SQLite, Oracle - by detecting the dialect from the
/// <see cref="DbConnection"/> at query time and rendering the
/// appropriate top-N clause.
/// </summary>
/// <remarks>
/// The set of saga types this diagnostic owns comes from
/// <see cref="SagaTableDefinition"/> registrations in DI - same
/// source the message-database boot path uses to materialise the
/// saga tables - so the diagnostic and the runtime always agree on
/// which sagas live where. Wolverine's runtime aggregator fans out
/// across this provider plus any registered Marten / EF Core /
/// RavenDB diagnostics so callers see one unified saga catalog
/// regardless of how many storages are wired up.
/// </remarks>
public sealed class DatabaseSagaStoreDiagnostics : ISagaStoreDiagnostics, ISagaIdleDiagnostics, ISagaRetentionSweeper
{
    private readonly IWolverineRuntime _runtime;
    private readonly IMessageDatabase _database;
    private readonly object _gate = new();
    private Dictionary<string, SagaTableDefinition>? _sagaIndex;

    public DatabaseSagaStoreDiagnostics(IWolverineRuntime runtime, IMessageDatabase database)
    {
        _runtime = runtime;
        _database = database;
    }

    /// <summary>
    /// The diagnostics for a lightweight message store. Under database-per-tenant storage the registered
    /// <see cref="IMessageStore" /> is a <see cref="MultiTenantedMessageStore" />, so the diagnostics read its main
    /// database, and a tenant's own database when a call names the tenant (GH-5558). Casting the registered store
    /// straight to <see cref="IMessageDatabase" /> threw for such a store, which took down
    /// <c>IWolverineRuntime.SagaStorage</c> for the whole application.
    /// </summary>
    public static DatabaseSagaStoreDiagnostics For(IWolverineRuntime runtime, IMessageStore store)
    {
        var database = store is MultiTenantedMessageStore multiTenanted ? multiTenanted.Main : store;
        return new DatabaseSagaStoreDiagnostics(runtime, (IMessageDatabase)database);
    }

    public Task<IReadOnlyList<SagaDescriptor>> GetRegisteredSagasAsync(CancellationToken ct)
    {
        var distinct = sagaIndex().Values
            .GroupBy(d => d.SagaType)
            .Select(g => g.First())
            .ToArray();

        var descriptors = distinct.Select(buildDescriptor).ToArray();
        return Task.FromResult<IReadOnlyList<SagaDescriptor>>(descriptors);
    }

    public async Task<SagaInstanceState?> ReadSagaAsync(string sagaTypeName, object identity, CancellationToken ct)
    {
        if (!sagaIndex().TryGetValue(sagaTypeName, out var definition)) return null;

        var conn = _database.DataSource.CreateConnection();
        await conn.OpenAsync(ct).ConfigureAwait(false);
        try
        {
            var qualified = qualifyTableName(definition.TableName);
            await using var cmd = conn.CreateCommand();
            var prefix = isOracle(conn) ? ":" : "@";
            cmd.CommandText =
                $"select {DatabaseConstants.Body}, {DatabaseConstants.Version} from {qualified} where {DatabaseConstants.Id} = {prefix}id";
            var p = cmd.CreateParameter();
            p.ParameterName = "id";

            // GH-5558: Oracle keeps a Guid id as RAW(16), the bytes of Guid.ToByteArray()
            p.Value = isOracle(conn) && idTypeOf(definition) == typeof(Guid) &&
                      Guid.TryParse(identity?.ToString(), out var guid)
                ? guid.ToByteArray()
                : identity?.ToString() ?? (object)DBNull.Value;
            cmd.Parameters.Add(p);

            await using var reader = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
            if (!await reader.ReadAsync(ct).ConfigureAwait(false)) return null;

            var body = readBody(reader, 0);
            return buildInstance(definition, identity!, body);
        }
        catch (DbException e) when (isMissingTableException(e))
        {
            // AddSagaType is explicitly optional, so a correctly configured
            // application can have a saga type whose table has not been
            // provisioned yet (the runtime creates it lazily on first
            // persist). To a diagnostic caller that state is
            // indistinguishable from "no instances", so report null rather
            // than surfacing the provider's undefined-table error (GH-3887).
            return null;
        }
        finally
        {
            await conn.CloseAsync().ConfigureAwait(false);
        }
    }

    public async Task<IReadOnlyList<SagaInstanceState>> ListSagaInstancesAsync(string sagaTypeName, int count, CancellationToken ct)
    {
        if (!sagaIndex().ContainsKey(sagaTypeName) || count <= 0)
            return Array.Empty<SagaInstanceState>();

        // Ordered by id (the QuerySagasAsync default) so the sample is repeatable.
        var result = await QuerySagasAsync(sagaTypeName, new DocumentQueryOptions(1, count), ct).ConfigureAwait(false);
        return result.Instances;
    }

    /// <summary>
    /// The lightweight tables hold the saga as an opaque JSON body with no <c>IQueryable</c> over it, so
    /// only paging, a total count and ordering by id are answered; a filter or an ordering is refused.
    /// </summary>
    public SagaQueryCapabilities GetQueryCapabilities(string sagaTypeName)
        => sagaIndex().ContainsKey(sagaTypeName)
            ? SagaQueryCapabilities.PagingOnly(
                "Lightweight saga storage keeps the saga as an opaque JSON body, so it can page and count and order by identity, but cannot filter or order on saga members. Use a storage with a queryable saga model (Marten, EF Core, RavenDB) for that.")
            : SagaQueryCapabilities.None;

    public Task<SagaQueryResult> QuerySagasAsync(string sagaTypeName, DocumentQueryOptions options, CancellationToken ct)
    {
        return QuerySagasAsync(sagaTypeName, options, false, ct);
    }

    public async Task<SagaQueryResult> QuerySagasAsync(string sagaTypeName, DocumentQueryOptions options,
        bool includeCompleted, CancellationToken ct)
    {
        var (pageNumber, pageSize) = SagaQueryCriteria.Page(options);
        SagaQueryCriteria.RefuseCriteria(options, GetQueryCapabilities(sagaTypeName));
        if (!sagaIndex().TryGetValue(sagaTypeName, out var definition))
            return SagaQueryResult.Empty(pageNumber, pageSize);

        var conn = _database.DataSource.CreateConnection();
        await conn.OpenAsync(ct).ConfigureAwait(false);
        try
        {
            var table = qualifyTableName(definition.TableName);

            long total;
            await using (var countCmd = conn.CreateCommand())
            {
                countCmd.CommandText = $"select count(*) from {table}{openOnly(includeCompleted)}";
                total = Convert.ToInt64(await countCmd.ExecuteScalarAsync(ct).ConfigureAwait(false));
            }

            var list = new List<SagaInstanceState>();
            await using var cmd = conn.CreateCommand();
            cmd.CommandText = renderPageQuery(conn, table, openOnly(includeCompleted), (pageNumber - 1) * pageSize, pageSize);
            await using var reader = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
            while (await reader.ReadAsync(ct).ConfigureAwait(false))
            {
                var id = await reader.IsDBNullAsync(0, ct).ConfigureAwait(false) ? string.Empty : readIdentity(reader, 0);
                var body = readBody(reader, 1);
                list.Add(buildInstance(definition, id, body));
            }

            return new SagaQueryResult(list, total, pageNumber, pageSize);
        }
        catch (DbException e) when (isMissingTableException(e))
        {
            // Same contract as ReadSagaAsync: a declared-but-never-persisted
            // saga has no table yet, which reads as "no instances" (GH-3887).
            return SagaQueryResult.Empty(pageNumber, pageSize);
        }
        finally
        {
            await conn.CloseAsync().ConfigureAwait(false);
        }
    }

    // GH-5558: idle saga diagnostics over the indexed last_modified column

    public bool SupportsIdleQueries(string sagaTypeName) => sagaIndex().ContainsKey(sagaTypeName);

    public async Task<long> CountIdleSagasAsync(string sagaTypeName, DateTimeOffset modifiedBefore, string? tenantId,
        CancellationToken ct)
    {
        if (!sagaIndex().TryGetValue(sagaTypeName, out var definition)) return 0;

        var database = await databaseForAsync(tenantId).ConfigureAwait(false);
        var conn = database.DataSource.CreateConnection();
        await conn.OpenAsync(ct).ConfigureAwait(false);
        try
        {
            await using var cmd = conn.CreateCommand();
            var where = renderLastModifiedFilter(cmd, SagaLastModifiedRange.Before(modifiedBefore));
            cmd.CommandText = $"select count(*) from {qualifyTableName(database, definition.TableName)}{where}";
            return Convert.ToInt64(await cmd.ExecuteScalarAsync(ct).ConfigureAwait(false));
        }
        catch (DbException e) when (isMissingTableException(e))
        {
            // No table yet means no instances (GH-3887)
            return 0;
        }
        finally
        {
            await conn.CloseAsync().ConfigureAwait(false);
        }
    }

    public async Task<SagaQueryResult> QuerySagasByLastModifiedAsync(string sagaTypeName, SagaLastModifiedRange range,
        DocumentQueryOptions options, CancellationToken ct)
    {
        var (tenantId, rest) = SagaQueryCriteria.TakeTenant(options);
        var (pageNumber, pageSize) = SagaQueryCriteria.Page(rest);
        SagaQueryCriteria.RefuseCriteria(rest, GetQueryCapabilities(sagaTypeName));
        if (!sagaIndex().TryGetValue(sagaTypeName, out var definition))
            return SagaQueryResult.Empty(pageNumber, pageSize);

        var database = await databaseForAsync(tenantId).ConfigureAwait(false);
        var conn = database.DataSource.CreateConnection();
        await conn.OpenAsync(ct).ConfigureAwait(false);
        try
        {
            var table = qualifyTableName(database, definition.TableName);

            long total;
            await using (var countCmd = conn.CreateCommand())
            {
                var countWhere = renderLastModifiedFilter(countCmd, range);
                countCmd.CommandText = $"select count(*) from {table}{countWhere}";
                total = Convert.ToInt64(await countCmd.ExecuteScalarAsync(ct).ConfigureAwait(false));
            }

            var list = new List<SagaInstanceState>();
            await using var cmd = conn.CreateCommand();
            var where = renderLastModifiedFilter(cmd, range);
            var select =
                $"select {DatabaseConstants.Id}, {DatabaseConstants.Body} from {table}{where} order by {SagaTableIndexes.LastModifiedColumn}, {DatabaseConstants.Id}";
            cmd.CommandText = renderPage(conn, select, (pageNumber - 1) * pageSize, pageSize);

            await using var reader = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
            while (await reader.ReadAsync(ct).ConfigureAwait(false))
            {
                var id = await reader.IsDBNullAsync(0, ct).ConfigureAwait(false) ? string.Empty : readIdentity(reader, 0);
                var body = readBody(reader, 1);
                list.Add(buildInstance(definition, id, body));
            }

            return new SagaQueryResult(list, total, pageNumber, pageSize);
        }
        catch (DbException e) when (isMissingTableException(e))
        {
            return SagaQueryResult.Empty(pageNumber, pageSize);
        }
        finally
        {
            await conn.CloseAsync().ConfigureAwait(false);
        }
    }

    // GH-5572: retained, completed sagas are deleted with one statement per saga table and database

    public string RetentionStoreName => "Database";

    public IReadOnlyList<Type> SagaTypesForRetention()
        => sagaIndex().Values.Select(x => x.SagaType).Distinct().ToArray();

    public async Task<long> DeleteCompletedSagasAsync(Type sagaType, DateTimeOffset completedBefore, CancellationToken ct)
    {
        if (!sagaIndex().TryGetValue(sagaType.FullName!, out var definition)) return 0;

        long deleted = 0;
        foreach (var database in allDatabases())
        {
            var conn = database.DataSource.CreateConnection();
            await conn.OpenAsync(ct).ConfigureAwait(false);
            try
            {
                await using var cmd = conn.CreateCommand();
                var prefix = isOracle(conn) ? ":" : "@";
                var p = cmd.CreateParameter();
                p.ParameterName = "completed_before";
                p.Value = isSqlite(conn)
                    ? completedBefore.UtcDateTime.ToString("yyyy-MM-dd HH:mm:ss.fff", System.Globalization.CultureInfo.InvariantCulture)
                    : isMySql(conn) ? completedBefore.UtcDateTime : completedBefore.ToUniversalTime();
                cmd.Parameters.Add(p);
                cmd.CommandText =
                    $"delete from {qualifyTableName(database, definition.TableName)} where {SagaTableIndexes.CompletedAtColumn} is not null and {SagaTableIndexes.CompletedAtColumn} < {prefix}completed_before";
                deleted += await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
            }
            catch (DbException e) when (isMissingTableException(e))
            {
                // No table yet means nothing to delete (GH-3887)
            }
            finally
            {
                await conn.CloseAsync().ConfigureAwait(false);
            }
        }

        return deleted;
    }

    /// <summary>
    /// The main database, plus every tenant database under database-per-tenant message storage
    /// </summary>
    private IReadOnlyList<IMessageDatabase> allDatabases()
    {
        var databases = new List<IMessageDatabase> { _database };
        if (_runtime.Storage is MultiTenantedMessageStore multiTenanted)
        {
            foreach (var database in multiTenanted.ActiveDatabases().OfType<IMessageDatabase>())
            {
                if (!databases.Contains(database)) databases.Add(database);
            }
        }

        return databases;
    }

    private static bool isSqlite(DbConnection conn)
        => (conn.GetType().FullName ?? string.Empty).Contains("Sqlite", StringComparison.OrdinalIgnoreCase);

    private static bool isMySql(DbConnection conn)
        => (conn.GetType().FullName ?? string.Empty).Contains("MySql", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// The database holding the tenant's lightweight saga tables: the main database for no tenant (or the
    /// default tenant), the tenant's own database under database-per-tenant message storage
    /// </summary>
    private async ValueTask<IMessageDatabase> databaseForAsync(string? tenantId)
    {
        if (DocumentQueryOptions.NormalizeTenantId(tenantId) is not { } tenant) return _database;

        if (_runtime.Storage is MultiTenantedMessageStore multiTenanted)
        {
            var store = await multiTenanted.GetDatabaseAsync(tenant).ConfigureAwait(false);
            return store as IMessageDatabase ?? throw new DocumentCriteriaNotSupportedException(
                nameof(DocumentQueryOptions.TenantId),
                $"the message store for tenant '{tenant}' is not a database with lightweight saga tables");
        }

        SagaQueryCriteria.RefuseTenant(tenant,
            "this message store is not multi-tenanted, so there are no per-tenant saga tables to read");
        return _database;
    }

    private static Type idTypeOf(SagaTableDefinition definition) => definition.IdMember switch
    {
        System.Reflection.PropertyInfo p => p.PropertyType,
        System.Reflection.FieldInfo f => f.FieldType,
        _ => typeof(object)
    };

    private static bool isOracle(DbConnection conn)
        => (conn.GetType().FullName ?? string.Empty).StartsWith("Oracle.", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// The saga id as the application knows it. Oracle keeps a Guid id as RAW(16) (Guid.ToByteArray()), which reads
    /// back as a byte array (GH-5558)
    /// </summary>
    private static object readIdentity(DbDataReader reader, int ordinal)
    {
        var value = reader.GetValue(ordinal);
        return value is byte[] { Length: 16 } bytes ? new Guid(bytes) : value;
    }

    /// <summary>
    /// Adds the range's parameters to <paramref name="cmd" /> and returns the where clause (with a leading space),
    /// which always leaves out retained, completed sagas. SQLite keeps last_modified as UTC text in a sortable format, so it
    /// compares against the same text; the other databases compare timestamps, passed in UTC.
    /// </summary>
    private static string renderLastModifiedFilter(DbCommand cmd, SagaLastModifiedRange range)
    {
        var dialect = cmd.Connection!.GetType().FullName ?? string.Empty;
        var prefix = isOracle(cmd.Connection!) ? ":" : "@";
        var isSqlite = dialect.Contains("Sqlite", StringComparison.OrdinalIgnoreCase);

        var clauses = new List<string>();
        void add(string name, string op, DateTimeOffset value)
        {
            var p = cmd.CreateParameter();
            p.ParameterName = name;
            p.Value = isSqlite
                ? value.UtcDateTime.ToString("yyyy-MM-dd HH:mm:ss.fff", System.Globalization.CultureInfo.InvariantCulture)
                : value.ToUniversalTime();
            cmd.Parameters.Add(p);
            clauses.Add($"{SagaTableIndexes.LastModifiedColumn} {op} {prefix}{name}");
        }

        // GH-5572: a retained, completed saga is not idle
        clauses.Add($"{SagaTableIndexes.CompletedAtColumn} is null");
        if (range.ModifiedAfter is { } after) add("modified_after", ">=", after);
        if (range.ModifiedBefore is { } before) add("modified_before", "<", before);

        return " where " + string.Join(" and ", clauses);
    }

    /// <summary>
    /// True when the exception is the backing database's flavor of
    /// "relation / table does not exist". Kept dialect-agnostic - same
    /// rationale as <see cref="renderTopNQuery"/>: this class works
    /// against every RDBMS message store without referencing the
    /// dialect packages, so it recognises each provider's shape rather
    /// than catching a provider-specific exception type.
    /// </summary>
    [UnconditionalSuppressMessage("Trimming", "IL2075",
        Justification = "Best-effort probe of the provider exception's 'Number' property (SqlClient 208, " +
                        "MySqlConnector 1146, Oracle 942). Provider exception types survive trimming in " +
                        "practice; if the property were removed the check falls through to the " +
                        "SqlState / message checks and, at worst, the exception propagates exactly as " +
                        "it did before this handling existed.")]
    private static bool isMissingTableException(DbException ex)
    {
        // Postgres: 42P01 undefined_table. MySQL: ER_NO_SUCH_TABLE surfaces
        // the ODBC-standard 42S02 ("base table or view not found").
        if (ex.SqlState is "42P01" or "42S02") return true;

        // SQL Server (208 "Invalid object name"), MySQL (1146), and Oracle
        // (ORA-00942) expose the provider error number on a 'Number'
        // property, but there is no shared base member for it.
        if (ex.GetType().GetProperty("Number")?.GetValue(ex) is int number &&
            number is 208 or 1146 or 942)
        {
            return true;
        }

        // SQLite only reports the generic error code 1 (SQLITE_ERROR) for a
        // missing table; its (non-localised) message is the discriminator.
        return ex.Message.Contains("no such table", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Body columns are <c>jsonb</c> on Postgres, <c>varbinary(max)</c>
    /// on SQL Server, and <c>TEXT</c> on SQLite / MySQL - read as
    /// either string or byte[] and normalise to UTF-8 string for
    /// <c>JsonDocument.Parse</c>.
    /// </summary>
    private static string readBody(DbDataReader reader, int ordinal)
    {
        if (reader.IsDBNull(ordinal)) return string.Empty;

        var fieldType = reader.GetFieldType(ordinal);
        if (fieldType == typeof(byte[]))
        {
            var bytes = (byte[])reader.GetValue(ordinal);
            return System.Text.Encoding.UTF8.GetString(bytes);
        }

        return reader.GetString(ordinal);
    }

    private string qualifyTableName(string tableName) => qualifyTableName(_database, tableName);

    private static string qualifyTableName(IMessageDatabase database, string tableName)
    {
        return string.IsNullOrEmpty(database.SchemaName) ? tableName : database.TableNameFor(tableName);
    }

    /// <summary>
    /// Render a page of rows ordered by id for the dialect this connection speaks. SQL Server and Oracle
    /// use <c>OFFSET .. FETCH</c>, the rest (Postgres / MySQL / SQLite) use <c>LIMIT .. OFFSET</c>.
    /// Detected from the connection's CLR type to keep this provider Wolverine-side without a circular
    /// reference into the dialect packages.
    /// </summary>
    private static string renderPageQuery(DbConnection conn, string table, string where, int offset, int count)
    {
        return renderPage(conn,
            $"select {DatabaseConstants.Id}, {DatabaseConstants.Body} from {table}{where} order by {DatabaseConstants.Id}",
            offset, count);
    }

    /// <summary>
    /// GH-5572. The where clause (with a leading space) that leaves out retained, completed sagas, or nothing
    /// </summary>
    private static string openOnly(bool includeCompleted)
        => includeCompleted ? string.Empty : $" where {SagaTableIndexes.CompletedAtColumn} is null";

    private static string renderPage(DbConnection conn, string select, int offset, int count)
    {
        var connTypeName = conn.GetType().FullName ?? string.Empty;

        if (connTypeName.Contains("SqlClient", StringComparison.OrdinalIgnoreCase) ||
            connTypeName.StartsWith("Oracle.", StringComparison.OrdinalIgnoreCase))
        {
            return $"{select} offset {offset} rows fetch next {count} rows only";
        }

        return $"{select} limit {count} offset {offset}";
    }

    private SagaDescriptor buildDescriptor(SagaTableDefinition definition)
    {
        return SagaDescriptorBuilder.Build(_runtime.Options.HandlerGraph, definition.SagaType, "Database");
    }

    private static SagaInstanceState buildInstance(SagaTableDefinition definition, object identity, string body)
    {
        JsonElement state;
        bool isCompleted = false;
        try
        {
            using var doc = JsonDocument.Parse(body);
            state = doc.RootElement.Clone();
            // Saga.IsCompleted backs onto a private field; if the
            // serialized state happened to roundtrip the field we'd
            // surface it here, but in the lightweight path the JSON
            // is an opaque body so leave isCompleted=false unless we
            // can spot the convention. Best-effort.
            if (state.ValueKind == JsonValueKind.Object &&
                state.TryGetProperty("_isCompleted", out var flag) &&
                flag.ValueKind == JsonValueKind.True)
            {
                isCompleted = true;
            }

            // GH-5572: a retained saga that completed carries Saga.CompletedAt in its body, under whatever
            // naming policy the saga serializer uses
            if (state.ValueKind == JsonValueKind.Object && state.EnumerateObject().Any(x =>
                    x.Name.Equals(nameof(Saga.CompletedAt), StringComparison.OrdinalIgnoreCase) &&
                    x.Value.ValueKind != JsonValueKind.Null))
            {
                isCompleted = true;
            }
        }
        catch (JsonException)
        {
            // Body wasn't valid JSON - surface a string-wrapped value
            // so the diagnostic doesn't tear down on a single broken
            // row.
            // AOT: use the source-generated JsonTypeInfo<string> below so this
            // catch-block fallback doesn't drag the reflection-based STJ
            // overload into the trim graph. Same chunk N (NodeRecord) pattern.
            state = JsonSerializer.SerializeToElement(body, DatabaseSagaStoreDiagnosticsJsonContext.Default.String);
        }

        return new SagaInstanceState(
            definition.SagaType.FullNameInCode(),
            identity,
            isCompleted,
            state,
            null);
    }

    private Dictionary<string, SagaTableDefinition> sagaIndex()
    {
        if (_sagaIndex is not null) return _sagaIndex;
        lock (_gate)
        {
            if (_sagaIndex is not null) return _sagaIndex;

            var index = new Dictionary<string, SagaTableDefinition>(StringComparer.Ordinal);

            // Two sources of truth:
            //   1. Explicit SagaTableDefinition registrations in DI -
            //      added by app code that wants a specific table name.
            //   2. Saga state types in the handler graph that the
            //      lightweight saga persistence frame provider claims -
            //      these are auto-generated SagaTableDefinitions that
            //      mirror what MessageDatabase.SagaSchemaFor builds at
            //      runtime, so the diagnostic can find sagas the user
            //      didn't manually pre-register.
            var explicitDefinitions = _runtime.Services.GetServices<SagaTableDefinition>();
            foreach (var def in explicitDefinitions)
            {
                index.TryAdd(def.SagaType.FullName!, def);
                index.TryAdd(def.SagaType.Name, def);
            }

            var providers = _runtime.Options.CodeGeneration.PersistenceProviders();
            var lightweight = providers.OfType<LightweightSagaPersistenceFrameProvider>().FirstOrDefault();
            if (lightweight is not null)
            {
                var container = _runtime.Options.HandlerGraph.Container;
                var sagaTypes = _runtime.Options.HandlerGraph.SagaChains() // GH-5133: includes Separated saga chains
                    .Select(c => c.SagaType)
                    .Distinct();

                foreach (var sagaType in sagaTypes)
                {
                    if (index.ContainsKey(sagaType.FullName!)) continue;

                    // Mirror the runtime's frame-provider precedence:
                    // Marten / EF Core / RavenDB register first (via
                    // InsertFirstPersistenceStrategy), then the
                    // lightweight RDBMS provider. The first provider
                    // whose CanPersist returns true wins at runtime -
                    // so we only own a saga here if the lightweight
                    // provider is that first winner. Without this
                    // check, a host wiring Marten or EF Core alongside
                    // a Postgres / SQL Server message store would see
                    // every saga listed twice (once per diagnostic).
                    Wolverine.Persistence.IPersistenceFrameProvider? firstOwner = null;
                    foreach (var p in providers)
                    {
                        try
                        {
                            if (p.CanPersist(sagaType, container, out _))
                            {
                                firstOwner = p;
                                break;
                            }
                        }
                        catch
                        {
                            // CanPersist may probe DI for a session/
                            // context - if resolution throws, treat
                            // the provider as not-applicable.
                        }
                    }
                    if (!ReferenceEquals(firstOwner, lightweight)) continue;

                    SagaTableDefinition def;
                    try
                    {
                        def = new SagaTableDefinition(sagaType, null);
                    }
                    catch
                    {
                        // Saga has no resolvable id member - skip
                        // rather than tearing down the whole
                        // diagnostic surface.
                        continue;
                    }

                    index.TryAdd(sagaType.FullName!, def);
                    index.TryAdd(sagaType.Name, def);
                }
            }

            _sagaIndex = index;
            return index;
        }
    }

}

/// <summary>
/// Source-generated JSON context covering the types <see cref="DatabaseSagaStoreDiagnostics"/>
/// serializes through STJ. Currently just <see cref="string"/> for the catch-block
/// fallback when a saga body fails JSON parsing — surfaces the raw text wrapped
/// as a JSON string. AOT-friendly path; mirrors chunk N's NodeRecordJsonContext.
/// </summary>
[JsonSerializable(typeof(string))]
internal partial class DatabaseSagaStoreDiagnosticsJsonContext : JsonSerializerContext;
