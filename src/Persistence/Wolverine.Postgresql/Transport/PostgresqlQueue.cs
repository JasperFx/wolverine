using JasperFx.Core;
using Microsoft.Extensions.Logging;
using Npgsql;
using Weasel.Postgresql;
using Weasel.Postgresql.Tables;
using Wolverine.Configuration;
using Wolverine.Runtime;
using Wolverine.Transports;
using Wolverine.Transports.Sending;

namespace Wolverine.Postgresql.Transport;

public class PostgresqlQueue : Endpoint, IBrokerQueue, IDatabaseBackedEndpoint
{
    internal static Uri ToUri(string name, string? databaseName)
    {
        return databaseName.IsEmpty()
            ? new Uri($"{PostgresqlTransport.ProtocolName}://{name}")
            : new Uri($"{PostgresqlTransport.ProtocolName}://{name}/{databaseName}");
    }

    private bool _hasInitialized;
    private IPostgresqlQueueSender? _sender;
    private ImHashMap<string, bool> _checkedDatabases = ImHashMap<string, bool>.Empty;
    private readonly Lazy<QueueTable> _queueTable;
    private readonly Lazy<ScheduledMessageTable> _scheduledMessageTable;

    public PostgresqlQueue(string name, PostgresqlTransport parent, EndpointRole role = EndpointRole.Application,
        string? databaseName = null) :
        base(ToUri(name, databaseName), role)
    {
        Parent = parent;
        var queueTableName = $"wolverine_queue_{name}";
        var scheduledTableName = $"wolverine_queue_{name}_scheduled";

        Mode = EndpointMode.Durable;
        Name = name;
        EndpointName = name;

        // Gotta be lazy so the schema names get set 
        _queueTable = new Lazy<QueueTable>(() => new QueueTable(Parent, queueTableName));
        _scheduledMessageTable =
            new Lazy<ScheduledMessageTable>(() => new ScheduledMessageTable(Parent, scheduledTableName));
    }

    public string Name { get; }

    internal PostgresqlTransport Parent { get; }

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
            await SetupAsync(runtime.LoggerFactory.CreateLogger<PostgresqlQueue>());
        }
        
        if (Parent.Databases != null)
        {
            var mtListener = new MultiTenantedQueueListener(
                runtime.LoggerFactory.CreateLogger<MultiTenantedQueueListener>(), this, Parent.Databases, runtime,
                receiver);

            await mtListener.StartAsync();
            return mtListener;
        }

        var listener = new PostgresqlQueueListener(this, runtime, receiver, DataSource, null);
        await listener.StartAsync();
        return listener;
    }

    private void buildSenderIfMissing()
    {
        if (Parent.Databases != null)
        {
            _sender = new MultiTenantedQueueSender(this, Parent.Databases);
        }
        else
        {
            _sender =  new PostgresqlQueueSender(this, DataSource, null);
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

    internal NpgsqlDataSource DataSource => Parent.Store?.NpgsqlDataSource ?? throw new InvalidOperationException("The PostgreSQL transport has not been successfully initialized");

    public ValueTask SendAsync(Envelope envelope)
    {
        buildSenderIfMissing();
        return _sender!.SendAsync(envelope);
    }

    private async ValueTask forEveryDatabase(Func<NpgsqlDataSource, string, Task> action)
    {
        if (Parent?.Store?.NpgsqlDataSource != null)
        {
            await action(Parent.Store.NpgsqlDataSource, Parent.Store.Identifier);
        }

        if (Parent?.Databases != null)
        {
            foreach (var database in Parent.Databases.ActiveDatabases().OfType<PostgresqlMessageStore>())
            {
                await action(database.NpgsqlDataSource, database.Identifier);
            }
        }
    }

    public ValueTask PurgeAsync(ILogger logger)
    {
        return forEveryDatabase(async (source, _) =>
        {
            var builder = new BatchBuilder();
            builder.Append($"delete from {QueueTable.Identifier}");
            builder.StartNewCommand();
            builder.Append($"delete from {ScheduledTable.Identifier}");

            await using var batch = builder.Compile();
            await using var conn = await source.OpenConnectionAsync();
            try
            {
                batch.Connection = conn;
                await batch.ExecuteNonQueryAsync();
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
                var queueDelta = await QueueTable.FindDeltaAsync(conn);
                if (queueDelta.HasChanges())
                {
                    returnValue = false;
                    return;
                }

                var scheduledDelta = await ScheduledTable.FindDeltaAsync(conn);

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
            await using var conn = await source.OpenConnectionAsync();

            await QueueTable.DropAsync(conn);
            await ScheduledTable.DropAsync(conn);

            await conn.CloseAsync();
        });
    }

    public async ValueTask SetupAsync(ILogger logger)
    {
        await forEveryDatabase(async (source, identifier) => {
        {
            await EnsureSchemaExists(identifier, source);
        }});
    }

    internal async Task EnsureSchemaExists(string identifier, NpgsqlDataSource source)
    {
        if (_checkedDatabases.Contains(identifier)) return;

        await using (var conn = await source.OpenConnectionAsync())
        {
            await QueueTable.ApplyChangesAsync(conn);
            await ScheduledTable.ApplyChangesAsync(conn);

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
                count += (long)await conn.CreateCommand($"select count(*) from {QueueTable.Identifier}").ExecuteScalarAsync();
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
                count += (long)await conn.CreateCommand($"select count(*) from {ScheduledTable.Identifier}").ExecuteScalarAsync();
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
    


