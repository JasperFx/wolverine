using System.Data.Common;
using System.Diagnostics.CodeAnalysis;
using System.Text.Json;
using System.Text.Json.Serialization;
using JasperFx.Core.Reflection;
using JasperFx.Descriptors;
using JasperFx.Documents;
using Microsoft.Extensions.DependencyInjection;
using Wolverine.Configuration.Capabilities;
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
public sealed class DatabaseSagaStoreDiagnostics : ISagaStoreDiagnostics
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
            var sql = $"select {DatabaseConstants.Body}, {DatabaseConstants.Version} from {qualified} where {DatabaseConstants.Id} = @id";
            await using var cmd = conn.CreateCommand();
            cmd.CommandText = sql;
            var p = cmd.CreateParameter();
            p.ParameterName = "id";
            p.Value = identity?.ToString() ?? (object)DBNull.Value;
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

    public async Task<SagaQueryResult> QuerySagasAsync(string sagaTypeName, DocumentQueryOptions options, CancellationToken ct)
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
                countCmd.CommandText = $"select count(*) from {table}";
                total = Convert.ToInt64(await countCmd.ExecuteScalarAsync(ct).ConfigureAwait(false));
            }

            var list = new List<SagaInstanceState>();
            await using var cmd = conn.CreateCommand();
            cmd.CommandText = renderPageQuery(conn, table, (pageNumber - 1) * pageSize, pageSize);
            await using var reader = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
            while (await reader.ReadAsync(ct).ConfigureAwait(false))
            {
                var id = await reader.IsDBNullAsync(0, ct).ConfigureAwait(false) ? string.Empty : reader.GetValue(0)!;
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

    private string qualifyTableName(string tableName)
    {
        return string.IsNullOrEmpty(_database.SchemaName) ? tableName : _database.TableNameFor(tableName);
    }

    /// <summary>
    /// Render a page of rows ordered by id for the dialect this connection speaks. SQL Server and Oracle
    /// use <c>OFFSET .. FETCH</c>, the rest (Postgres / MySQL / SQLite) use <c>LIMIT .. OFFSET</c>.
    /// Detected from the connection's CLR type to keep this provider Wolverine-side without a circular
    /// reference into the dialect packages.
    /// </summary>
    private static string renderPageQuery(DbConnection conn, string table, int offset, int count)
    {
        var connTypeName = conn.GetType().FullName ?? string.Empty;
        var select = $"select {DatabaseConstants.Id}, {DatabaseConstants.Body} from {table} order by {DatabaseConstants.Id}";

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
