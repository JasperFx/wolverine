using System.Data.Common;
using System.Text.Json;
using System.Text.Json.Serialization;
using JasperFx.Core.Reflection;
using JasperFx.Descriptors;
using Microsoft.Extensions.DependencyInjection;
using Wolverine.Persistence.Sagas;
using Wolverine.Runtime;

namespace Wolverine.RDBMS.Sagas;

/// <summary>
/// Lightweight-RDBMS implementation of <see cref="ISagaStoreDiagnostics"/>.
/// Surfaces saga state stored in Wolverine's per-saga-type tables
/// (one row per saga, identified by <c>id</c> with a JSON <c>body</c>
/// column) for monitoring tools. Works against any
/// <see cref="IMessageDatabase"/>-backed store — Postgres, SQL Server,
/// MySQL, SQLite, Oracle — by detecting the dialect from the
/// <see cref="DbConnection"/> at query time and rendering the
/// appropriate top-N clause.
/// </summary>
/// <remarks>
/// The set of saga types this diagnostic owns comes from
/// <see cref="SagaTableDefinition"/> registrations in DI — same
/// source the message-database boot path uses to materialise the
/// saga tables — so the diagnostic and the runtime always agree on
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

    public Task<IReadOnlyList<SagaTypeDescriptor>> GetRegisteredSagaTypesAsync(CancellationToken ct)
    {
        var distinct = sagaIndex().Values
            .GroupBy(d => d.SagaType)
            .Select(g => g.First())
            .ToArray();

        var descriptors = distinct.Select(buildDescriptor).ToArray();
        return Task.FromResult<IReadOnlyList<SagaTypeDescriptor>>(descriptors);
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
            var cmd = conn.CreateCommand();
            cmd.CommandText = sql;
            var p = cmd.CreateParameter();
            p.ParameterName = "id";
            p.Value = identity?.ToString() ?? (object)DBNull.Value;
            cmd.Parameters.Add(p);

            await using var reader = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
            if (!await reader.ReadAsync(ct).ConfigureAwait(false)) return null;

            var body = readBody(reader, 0);
            return buildInstance(definition, identity, body);
        }
        finally
        {
            await conn.CloseAsync().ConfigureAwait(false);
        }
    }

    public async Task<IReadOnlyList<SagaInstanceState>> ListSagaInstancesAsync(string sagaTypeName, int count, CancellationToken ct)
    {
        if (!sagaIndex().TryGetValue(sagaTypeName, out var definition))
            return Array.Empty<SagaInstanceState>();

        var clamped = count <= 0 ? 0 : Math.Min(count, 1000);
        if (clamped == 0) return Array.Empty<SagaInstanceState>();

        var conn = _database.DataSource.CreateConnection();
        await conn.OpenAsync(ct).ConfigureAwait(false);
        try
        {
            var sql = renderTopNQuery(conn, definition, clamped);
            var cmd = conn.CreateCommand();
            cmd.CommandText = sql;

            var list = new List<SagaInstanceState>();
            await using var reader = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
            while (await reader.ReadAsync(ct).ConfigureAwait(false))
            {
                var id = reader.IsDBNull(0) ? string.Empty : reader.GetValue(0)!;
                var body = readBody(reader, 1);
                list.Add(buildInstance(definition, id, body));
            }
            return list;
        }
        finally
        {
            await conn.CloseAsync().ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Body columns are <c>jsonb</c> on Postgres, <c>varbinary(max)</c>
    /// on SQL Server, and <c>TEXT</c> on SQLite / MySQL — read as
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
        var schema = _database.SchemaName;
        return string.IsNullOrEmpty(schema) ? tableName : $"{schema}.{tableName}";
    }

    /// <summary>
    /// Render a portable "top-N rows" SELECT for the dialect this
    /// connection speaks. SQL Server uses <c>SELECT TOP N</c>,
    /// Oracle uses <c>FETCH FIRST N ROWS ONLY</c>, and the rest
    /// (Postgres / MySQL / SQLite) use <c>LIMIT N</c>. Detected from
    /// the connection's CLR type to keep this provider Wolverine-side
    /// without a circular reference into the dialect packages.
    /// </summary>
    private string renderTopNQuery(DbConnection conn, SagaTableDefinition definition, int count)
    {
        var connTypeName = conn.GetType().FullName ?? string.Empty;
        var idCol = DatabaseConstants.Id;
        var bodyCol = DatabaseConstants.Body;
        var table = qualifyTableName(definition.TableName);

        if (connTypeName.Contains("SqlClient", StringComparison.OrdinalIgnoreCase))
        {
            return $"select top {count} {idCol}, {bodyCol} from {table}";
        }
        if (connTypeName.StartsWith("Oracle.", StringComparison.OrdinalIgnoreCase))
        {
            return $"select {idCol}, {bodyCol} from {table} fetch first {count} rows only";
        }
        // Postgres (Npgsql), MySQL (MySqlConnector), SQLite — all LIMIT
        return $"select {idCol}, {bodyCol} from {table} limit {count}";
    }

    private SagaTypeDescriptor buildDescriptor(SagaTableDefinition definition)
    {
        var (starting, continuing) = SagaMessageBuckets.For(definition.SagaType, _runtime.Options.HandlerGraph);
        return new SagaTypeDescriptor(
            TypeDescriptor.For(definition.SagaType),
            starting,
            continuing,
            "Database");
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
            // Body wasn't valid JSON — surface a string-wrapped value
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
            //   1. Explicit SagaTableDefinition registrations in DI —
            //      added by app code that wants a specific table name.
            //   2. Saga state types in the handler graph that the
            //      lightweight saga persistence frame provider claims —
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
                var sagaTypes = _runtime.Options.HandlerGraph.Chains
                    .OfType<SagaChain>()
                    .Select(c => c.SagaType)
                    .Distinct();

                foreach (var sagaType in sagaTypes)
                {
                    if (index.ContainsKey(sagaType.FullName!)) continue;

                    // Mirror the runtime's frame-provider precedence:
                    // Marten / EF Core / RavenDB register first (via
                    // InsertFirstPersistenceStrategy), then the
                    // lightweight RDBMS provider. The first provider
                    // whose CanPersist returns true wins at runtime —
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
                            // context — if resolution throws, treat
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
                        // Saga has no resolvable id member — skip
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
