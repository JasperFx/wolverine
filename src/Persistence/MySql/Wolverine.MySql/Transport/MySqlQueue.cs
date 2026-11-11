using ImTools;
using JasperFx.Core;
using Microsoft.Extensions.Logging;
using MySqlConnector;
using Weasel.Core;
using Weasel.MySql;
using Weasel.MySql.Tables;
using Wolverine.Configuration;
using Wolverine.Runtime;
using Wolverine.Transports;
using Wolverine.Transports.Sending;

namespace Wolverine.MySql.Transport;

public class MySqlQueue : Endpoint, IBrokerQueue, IDatabaseBackedEndpoint, IStorageBackedQueue
{
    internal static Uri ToUri(string name, string? databaseName)
    {
        return databaseName.IsEmpty()
            ? new Uri($"{MySqlTransport.ProtocolName}://{name}")
            : new Uri($"{MySqlTransport.ProtocolName}://{name}/{databaseName}");
    }

    private bool _hasInitialized;
    private IMySqlQueueSender? _sender;
    private ImHashMap<string, bool> _checkedDatabases = ImHashMap<string, bool>.Empty;
    private ImHashMap<string, bool> _queueOrdering = ImHashMap<string, bool>.Empty;
    private ImHashMap<string, QueueTable> _queueTables = ImHashMap<string, QueueTable>.Empty;
    private ImHashMap<string, ScheduledMessageTable> _scheduledTables = ImHashMap<string, ScheduledMessageTable>.Empty;
    private readonly string _queueTableName;
    private readonly string _scheduledTableName;
    private readonly Lazy<QueueTable> _queueTable;
    private readonly Lazy<ScheduledMessageTable> _scheduledMessageTable;

    public MySqlQueue(string name, MySqlTransport parent, EndpointRole role = EndpointRole.Application,
        string? databaseName = null) :
        base(ToUri(name, databaseName), role)
    {
        Parent = parent;
        // MySQL refuses table names over 64 characters, which these passed for queue names over 38 (the
        // scheduled table) or 48 characters. Shorten() leaves every name that fit unchanged.
        _queueTableName = MySqlIdentifier.Shorten($"wolverine_queue_{name}");
        _scheduledTableName = MySqlIdentifier.Shorten($"wolverine_queue_{name}_scheduled");

        Mode = EndpointMode.Durable;
        Name = name;
        EndpointName = name;
        BrokerRole = "queue";

        _queueTable = new Lazy<QueueTable>(() => new QueueTable(Parent, _queueTableName));
        _scheduledMessageTable =
            new Lazy<ScheduledMessageTable>(() => new ScheduledMessageTable(Parent, _scheduledTableName));
    }

    public string Name { get; }

    internal MySqlTransport Parent { get; }

    internal Table QueueTable => _queueTable.Value;

    internal Table ScheduledTable => _scheduledMessageTable.Value;

    /// <summary>
    /// GH-3859. MySQL has no schema-inside-database nesting — a schema IS a database — so the single
    /// <see cref="MySqlTransport.TransportSchemaName"/> that works for the other providers resolves to one
    /// physical database for every tenant, and all tenants end up sharing one queue table. On a
    /// multi-tenanted host each data source therefore has to resolve its queue tables inside its *own*
    /// database. Single database hosts are unaffected and keep using TransportSchemaName.
    /// </summary>
    internal string SchemaFor(MySqlDataSource source)
    {
        if (Parent.Databases == null) return Parent.TransportSchemaName;

        var database = new MySqlConnectionStringBuilder(source.ConnectionString).Database;

        return database.IsEmpty() ? Parent.TransportSchemaName : database;
    }

    internal QueueTable QueueTableFor(MySqlDataSource source)
    {
        var schema = SchemaFor(source);
        if (_queueTables.TryFind(schema, out var table)) return table;

        // Built lazily on first use, after configuration is complete, so the final transport option applies.
        table = new QueueTable(schema, _queueTableName, Parent.EnableDeterministicQueueOrdering,
            !Parent.DeterministicQueueOrderingIsExplicit);
        _queueTables = _queueTables.AddOrUpdate(schema, table);

        return table;
    }

    internal ScheduledMessageTable ScheduledTableFor(MySqlDataSource source)
    {
        var schema = SchemaFor(source);
        if (_scheduledTables.TryFind(schema, out var table)) return table;

        table = new ScheduledMessageTable(schema, _scheduledTableName, Parent.EnableDeterministicQueueOrdering,
            !Parent.DeterministicQueueOrderingIsExplicit);
        _scheduledTables = _scheduledTables.AddOrUpdate(schema, table);

        return table;
    }

    /// <summary>
    ///     GH-5192. Does this database dequeue with the <c>queue_order</c> tie-breaker? Only when it is enabled and
    ///     neither queue table exists without the column: 7.0 adopts an existing classic table as it stands, and
    ///     each tenant database may differ.
    /// </summary>
    internal async Task<bool> UsesQueueOrderingAsync(string identifier, MySqlDataSource source)
    {
        if (!Parent.EnableDeterministicQueueOrdering) return false;
        if (_queueOrdering.TryFind(identifier, out var known)) return known;

        await using var conn = await source.OpenConnectionAsync();
        try
        {
            await using var cmd = conn.CreateCommand();
            cmd.CommandText =
                $"SELECT table_name, MAX(column_name = '{QueueOrdering.QueueOrderColumn}') FROM information_schema.columns " +
                "WHERE table_schema = @schema AND table_name IN (@ready, @scheduled) GROUP BY table_name";
            cmd.Parameters.AddWithValue("@schema", SchemaFor(source));
            cmd.Parameters.AddWithValue("@ready", _queueTableName);
            cmd.Parameters.AddWithValue("@scheduled", _scheduledTableName);

            var ordered = true;
            await using var reader = await cmd.ExecuteReaderAsync();
            while (await reader.ReadAsync())
            {
                ordered = ordered && Convert.ToInt64(reader.GetValue(1)) == 1;
            }

            _queueOrdering = _queueOrdering.AddOrUpdate(identifier, ordered);
            return ordered;
        }
        finally
        {
            await conn.CloseAsync();
        }
    }

    protected override bool supportsMode(EndpointMode mode)
    {
        return mode == EndpointMode.Durable || mode == EndpointMode.BufferedInMemory;
    }

    /// <summary>
    ///     The maximum number of messages to receive in a single batch when listening
    ///     in either buffered or durable modes. The default is 20.
    /// </summary>
    public int MaximumMessagesToReceive { get; set; } = 20;

    /// <summary>
    ///     How often to poll for new messages when the queue is idle.
    ///     If null, falls back to DurabilitySettings.ScheduledJobPollingTime (default 5s).
    /// </summary>
    public TimeSpan? PollingInterval { get; set; }

    public override async ValueTask<IListener> BuildListenerAsync(IWolverineRuntime runtime, IReceiver receiver)
    {
        if (Parent.AutoProvision)
        {
            await SetupAsync(runtime.LoggerFactory.CreateLogger<MySqlQueue>());
        }

        if (Parent.Databases != null)
        {
            var mtListener = new MultiTenantedQueueListener(
                runtime.LoggerFactory.CreateLogger<MultiTenantedQueueListener>(), this, Parent.Databases, runtime,
                receiver);

            await mtListener.StartAsync();
            return mtListener;
        }

        var listener = new MySqlQueueListener(this, runtime, receiver, DataSource, null);
        await listener.StartAsync();
        return listener;
    }

    private void buildSenderIfMissing()
    {
        // "IfMissing" was aspirational: without this guard every call built ANOTHER sender, and on a
        // multi-tenanted host that meant a fresh MultiTenantedQueueSender -- with its own SemaphoreSlim and
        // CancellationTokenSource, neither ever disposed, and an empty per-database cache -- for every
        // single send. Matches the SQL Server and SQLite twins, which have always had the guard.
        if (_sender != null) return;

        if (Parent.Databases != null)
        {
            _sender = new MultiTenantedQueueSender(this, Parent.Databases);
        }
        else
        {
            _sender = new MySqlQueueSender(this, DataSource, null);
        }
    }

    protected override ISender CreateSender(IWolverineRuntime runtime)
    {
        buildSenderIfMissing();
        return _sender!;
    }

    public override async ValueTask InitializeAsync(ILogger logger)
    {
        if (_hasInitialized)
        {
            return;
        }

        if (Parent.AutoProvision)
        {
            await SetupAsync(logger);
        }

        if (Parent.AutoPurgeAllQueues)
        {
            await PurgeAsync(logger);
        }

        _hasInitialized = true;
    }

    internal MySqlDataSource DataSource => Parent.Store?.MySqlDataSource ?? throw new InvalidOperationException("The MySQL transport has not been successfully initialized");

    public ValueTask SendAsync(Envelope envelope)
    {
        buildSenderIfMissing();
        return _sender!.SendAsync(envelope);
    }

    /// <summary>
    /// GH-3815. These two sources overlap: <c>MultiTenantedMessageStore.ActiveDatabases()</c> yields
    /// <c>Main</c> first, and <see cref="MySqlTransport.Databases"/> is only ever assigned alongside
    /// <c>Store = mt.Main</c>. Visiting both therefore hit the main database twice — doubling
    /// <see cref="CountAsync"/>/<see cref="ScheduledCountAsync"/>, which <see cref="GetAttributesAsync"/>
    /// reports as user visible queue depth, and running every schema check against it twice. The
    /// SqlServer and Sqlite queues already branch this way.
    /// </summary>
    private async ValueTask forEveryDatabase(Func<MySqlDataSource, string, Task> action)
    {
        if (Parent?.Databases != null)
        {
            foreach (var database in Parent.Databases.ActiveDatabases().OfType<MySqlMessageStore>())
            {
                await action(database.MySqlDataSource, database.Identifier);
            }
        }
        else if (Parent?.Store?.MySqlDataSource != null)
        {
            await action(Parent.Store.MySqlDataSource, Parent.Store.Identifier);
        }
    }

    public ValueTask PurgeAsync(ILogger logger)
    {
        return forEveryDatabase(async (source, _) =>
        {
            await using var conn = await source.OpenConnectionAsync();
            try
            {
                await using var cmd1 = conn.CreateCommand();
                cmd1.CommandText = $"DELETE FROM {QueueTableFor(source).Identifier.QualifiedName}";
                await cmd1.ExecuteNonQueryAsync();

                await using var cmd2 = conn.CreateCommand();
                cmd2.CommandText = $"DELETE FROM {ScheduledTableFor(source).Identifier.QualifiedName}";
                await cmd2.ExecuteNonQueryAsync();
            }
            finally
            {
                await conn.CloseAsync();
            }
        });
    }

    public async ValueTask<Dictionary<string, string>> GetAttributesAsync()
    {
        var count = await CountAsync();
        var scheduled = await ScheduledCountAsync();

        return new Dictionary<string, string>
            { { "Name", Name }, { "Count", count.ToString() }, { "Scheduled", scheduled.ToString() } };
    }

    public async ValueTask<bool> CheckAsync()
    {
        var returnValue = true;
        await forEveryDatabase(async (source, _) =>
        {
            await using var conn = await source.OpenConnectionAsync();
            try
            {
                // GH-5192: through CreateDeltaAsync, not Table.FindDeltaAsync, so an adopted classic table is
                // not reported as a pending change
                var migration = await SchemaMigration.DetermineAsync(conn, CancellationToken.None,
                    QueueTableFor(source), ScheduledTableFor(source));
                returnValue = returnValue && migration.Difference == SchemaPatchDifference.None;
            }
            finally
            {
                await conn.CloseAsync();
            }
        });

        return returnValue;
    }

    public async ValueTask TeardownAsync(ILogger logger)
    {
        await forEveryDatabase(async (source, _) =>
        {
            await using var conn = await source.OpenConnectionAsync();

            await QueueTableFor(source).DropAsync(conn);
            await ScheduledTableFor(source).DropAsync(conn);

            await conn.CloseAsync();
        });
    }

    public async ValueTask SetupAsync(ILogger logger)
    {
        // Deliberately bypasses the _checkedDatabases memo. SetupAsync is the explicit
        // "make sure these tables exist right now" call - resource setup, and
        // IHost.ClearAllWolverineStorageAsync() - so it has to re-apply against a database
        // whose queue tables were dropped after we last looked.
        await forEveryDatabase(applySchemaChangesAsync);
    }

    internal async Task EnsureSchemaExists(string identifier, MySqlDataSource source)
    {
        if (_checkedDatabases.Contains(identifier)) return;

        await applySchemaChangesAsync(source, identifier);
    }

    private async Task applySchemaChangesAsync(MySqlDataSource source, string identifier)
    {
        await using (var conn = await source.OpenConnectionAsync())
        {
            await QueueTableFor(source).ApplyChangesAsync(conn);
            await ScheduledTableFor(source).ApplyChangesAsync(conn);

            await conn.CloseAsync();
        }

        _checkedDatabases = _checkedDatabases.AddOrUpdate(identifier, true);
    }

    public async Task<long> CountAsync()
    {
        var count = 0L;
        await forEveryDatabase(async (source, _) =>
        {
            await using var conn = await source.OpenConnectionAsync();

            try
            {
                await using var cmd = conn.CreateCommand();
                cmd.CommandText = $"SELECT COUNT(*) FROM {QueueTableFor(source).Identifier.QualifiedName}";
                count += Convert.ToInt64(await cmd.ExecuteScalarAsync());
            }
            finally
            {
                await conn.CloseAsync();
            }
        });

        return count;
    }

    public async Task<long> ScheduledCountAsync()
    {
        var count = 0L;
        await forEveryDatabase(async (source, _) =>
        {
            await using var conn = await source.OpenConnectionAsync();
            try
            {
                await using var cmd = conn.CreateCommand();
                cmd.CommandText = $"SELECT COUNT(*) FROM {ScheduledTableFor(source).Identifier.QualifiedName}";
                count += Convert.ToInt64(await cmd.ExecuteScalarAsync());
            }
            finally
            {
                await conn.CloseAsync();
            }
        });

        return count;
    }

    public Task ScheduleRetryAsync(Envelope envelope, CancellationToken cancellation)
    {
        buildSenderIfMissing();
        return _sender!.ScheduleRetryAsync(envelope, cancellation);
    }
}
