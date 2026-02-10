using System.Data.Common;
using ImTools;
using JasperFx;
using JasperFx.Core;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging;
using Weasel.Core;
using Weasel.Core.Migrations;
using Weasel.Sqlite;
using Weasel.Sqlite.Tables;
using Wolverine.Configuration;
using Wolverine.Runtime;
using Wolverine.Transports;
using Wolverine.Transports.Sending;

namespace Wolverine.Sqlite.Transport;

public class SqliteQueue : Endpoint, IBrokerQueue, IDatabaseBackedEndpoint
{
    internal static Uri ToUri(string name, string? databaseName)
    {
        return databaseName.IsEmpty()
            ? new Uri($"{SqliteTransport.ProtocolName}://{name}")
            : new Uri($"{SqliteTransport.ProtocolName}://{name}/{databaseName}");
    }

    private bool _hasInitialized;
    private ISqliteQueueSender? _sender;
    private ImHashMap<string, bool> _checkedDatabases = ImHashMap<string, bool>.Empty;
    private readonly Lazy<QueueTable> _queueTable;
    private readonly Lazy<ScheduledMessageTable> _scheduledMessageTable;

    public SqliteQueue(string name, SqliteTransport parent, EndpointRole role = EndpointRole.Application,
        string? databaseName = null) :
        base(ToUri(name, databaseName), role)
    {
        Parent = parent;
        var queueTableName = $"wolverine_queue_{name}";
        var scheduledTableName = $"wolverine_queue_{name}_scheduled";

        Mode = EndpointMode.Durable;
        Name = name;
        EndpointName = name;

        _queueTable = new Lazy<QueueTable>(() => new QueueTable(Parent, queueTableName));
        _scheduledMessageTable =
            new Lazy<ScheduledMessageTable>(() => new ScheduledMessageTable(Parent, scheduledTableName));
    }

    public string Name { get; }

    internal SqliteTransport Parent { get; }

    internal Table QueueTable => _queueTable.Value;

    internal Table ScheduledTable => _scheduledMessageTable.Value;

    protected override bool supportsMode(EndpointMode mode)
    {
        return mode == EndpointMode.Durable || mode == EndpointMode.BufferedInMemory;
    }

    /// <summary>
    ///     The maximum number of messages to receive in a single batch when listening
    ///     in either buffered or durable modes. The default is 20.
    /// </summary>
    public int MaximumMessagesToReceive { get; set; } = 20;

    public override async ValueTask<IListener> BuildListenerAsync(IWolverineRuntime runtime, IReceiver receiver)
    {
        if (Parent.AutoProvision)
        {
            await SetupAsync(runtime.LoggerFactory.CreateLogger<SqliteQueue>());
        }

        var listener = new SqliteQueueListener(this, runtime, receiver, DataSource, null);
        await listener.StartAsync();
        return listener;
    }

    private void buildSenderIfMissing()
    {
        _sender = new SqliteQueueSender(this, DataSource, null);
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

    internal DbDataSource DataSource => Parent.Store?.DataSource ?? throw new InvalidOperationException("The SQLite transport has not been successfully initialized");

    public ValueTask SendAsync(Envelope envelope)
    {
        buildSenderIfMissing();
        return _sender!.SendAsync(envelope);
    }

    private async ValueTask forEveryDatabase(Func<DbDataSource, string, Task> action)
    {
        if (Parent?.Store?.DataSource != null)
        {
            await action(Parent.Store.DataSource, Parent.Store.Identifier);
        }
    }

    public ValueTask PurgeAsync(ILogger logger)
    {
        return forEveryDatabase(async (source, _) =>
        {
            await using var conn = await source.OpenConnectionAsync().ConfigureAwait(false);

            await conn.CreateCommand($"delete from {QueueTable.Identifier}").ExecuteNonQueryAsync();
            await conn.CreateCommand($"delete from {ScheduledTable.Identifier}").ExecuteNonQueryAsync();
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
            await using var conn = await source.OpenConnectionAsync().ConfigureAwait(false);
            try
            {
                var queueDelta = await QueueTable.FindDeltaAsync((SqliteConnection)conn);
                if (queueDelta.HasChanges())
                {
                    returnValue = false;
                    return;
                }

                var scheduledDelta = await ScheduledTable.FindDeltaAsync((SqliteConnection)conn);

                returnValue = returnValue && !scheduledDelta.HasChanges();
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
            await using var conn = await source.OpenConnectionAsync().ConfigureAwait(false);

            await QueueTable.DropAsync((SqliteConnection)conn);
            await ScheduledTable.DropAsync((SqliteConnection)conn);
        });
    }

    public async ValueTask SetupAsync(ILogger logger)
    {
        await forEveryDatabase(async (source, identifier) => { await EnsureSchemaExists(identifier, source); });
    }

    internal async Task EnsureSchemaExists(string identifier, DbDataSource source)
    {
        if (_checkedDatabases.Contains(identifier)) return;

        await using (var conn = (SqliteConnection)await source.OpenConnectionAsync().ConfigureAwait(false))
        {
            var queueMigration = await SchemaMigration.DetermineAsync(conn, default(CancellationToken), QueueTable);
            if (queueMigration.Difference != SchemaPatchDifference.None)
            {
                await new SqliteMigrator().ApplyAllAsync(conn, queueMigration, AutoCreate.CreateOrUpdate);
            }

            var scheduledMigration = await SchemaMigration.DetermineAsync(conn, default(CancellationToken), ScheduledTable);
            if (scheduledMigration.Difference != SchemaPatchDifference.None)
            {
                await new SqliteMigrator().ApplyAllAsync(conn, scheduledMigration, AutoCreate.CreateOrUpdate);
            }
        }

        _checkedDatabases = _checkedDatabases.AddOrUpdate(identifier, true);
    }

    public async Task<long> CountAsync()
    {
        var count = 0L;
        await forEveryDatabase(async (source, _) =>
        {
            await using var conn = await source.OpenConnectionAsync().ConfigureAwait(false);

            try
            {
                var result = await conn.CreateCommand($"select count(*) from {QueueTable.Identifier}")
                    .ExecuteScalarAsync();
                count += Convert.ToInt64(result);
            }
            catch
            {
                // Swallow exceptions during count - table might not exist yet
            }
        });

        return count;
    }

    public async Task<long> ScheduledCountAsync()
    {
        var count = 0L;
        await forEveryDatabase(async (source, _) =>
        {
            await using var conn = await source.OpenConnectionAsync().ConfigureAwait(false);
            try
            {
                var result = await conn.CreateCommand($"select count(*) from {ScheduledTable.Identifier}")
                    .ExecuteScalarAsync();
                count += Convert.ToInt64(result);
            }
            catch
            {
                // Swallow exceptions during count - table might not exist yet
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
