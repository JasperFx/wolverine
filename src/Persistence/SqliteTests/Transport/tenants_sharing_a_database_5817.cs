using JasperFx.Core;
using JasperFx.Core.Reflection;
using Microsoft.Extensions.Hosting;
using Shouldly;
using Weasel.Sqlite;
using Wolverine;
using Wolverine.Persistence.Durability;
using Wolverine.Runtime;
using Wolverine.Sqlite;
using Wolverine.Sqlite.Transport;
using Wolverine.Tracking;
using Wolverine.Util;

namespace SqliteTests.Transport;

/// <summary>
/// GH-5817, the SQLite twin of the SQL Server test of the same name. Two tenant ids registered against the SAME
/// database file each get their own tenant message store, and both stores answer to the same name (the database
/// URI). <see cref="MultiTenantedQueueListener"/> started one queue listener per store at startup but kept them
/// in a map keyed by that name, so stop/dispose only ever reached one of them. The other kept polling the
/// shared database's queue table after the host was stopped.
/// </summary>
[Collection("sqlite")]
public class tenants_sharing_a_database_5817 : SqliteContext, IAsyncLifetime
{
    private const string QueueName = "sharedpool5817";

    private IHost _host = null!;
    private SqliteTestDatabase _main = null!;
    private SqliteTestDatabase _pool = null!;
    private SqliteTestDatabase _green = null!;

    public async ValueTask InitializeAsync()
    {
        _main = Servers.CreateDatabase("sqlite_5817_main");
        _pool = Servers.CreateDatabase("sqlite_5817_pool");
        _green = Servers.CreateDatabase("sqlite_5817_green");

        _host = await Host.CreateDefaultBuilder()
            .UseWolverine(opts =>
            {
                opts.PersistMessagesWithSqlite(_main.ConnectionString)
                    .RegisterStaticTenants(tenants =>
                    {
                        // "red" and "blue" share one pool database
                        tenants.Register("red", _pool.ConnectionString);
                        tenants.Register("blue", _pool.ConnectionString);
                        tenants.Register("green", _green.ConnectionString);
                    })
                    .EnableMessageTransport(x => x.AutoProvision());

                // Buffered, so a pop deletes the row outright. The durable pop of a leaked listener writes the
                // inbox row with the stopped runtime's token, rolls back and leaves the row where it was, which
                // would hide the leak from the assertion below even though the listener is still polling
                opts.ListenToSqliteQueue(QueueName).PollingInterval(100.Milliseconds()).BufferedInMemory();

                opts.Discovery.DisableConventionalDiscovery().IncludeType<SharedPoolMessageHandler>();
            }).StartAsync();
    }

    public async ValueTask DisposeAsync()
    {
        await _host.StopAsync();
        _host.Dispose();

        _main.Dispose();
        _pool.Dispose();
        _green.Dispose();
    }

    [Fact]
    public async Task no_queue_listener_outlives_the_host_when_two_tenants_share_a_database()
    {
        var runtime = _host.GetRuntime();
        var queue = runtime.Options.Transports.OfType<SqliteTransport>().Single().Queues[QueueName];

        var stores = runtime.Stores.Main.As<MultiTenantedMessageStore>();
        var red = await stores.Source.FindAsync("red");
        var blue = await stores.Source.FindAsync("blue");
        red.Name.ShouldBe(blue.Name);

        runtime.Endpoints.FindListeningAgent(SqliteQueue.ToUri(QueueName, null))
            .ShouldNotBeNull();

        await _host.StopAsync(TestContext.Current.CancellationToken);

        using var dataSource = new SqliteDataSource(_pool.ConnectionString);
        var queueTable = queue.QueueTable.Identifier.QualifiedName;
        await executeAsync(dataSource, $"delete from {queueTable}");

        // Nothing is listening any more, so a message put on the shared database's queue has to stay there
        var envelope = new Envelope(new SharedPoolMessage(Guid.NewGuid()))
        {
            Id = Guid.NewGuid(),
            ContentType = runtime.Options.DefaultSerializer.ContentType,
            MessageType = typeof(SharedPoolMessage).ToMessageTypeName(),
            Destination = SqliteQueue.ToUri(QueueName, red.Name)
        };
        envelope.Data = runtime.Options.DefaultSerializer.Write(envelope);

        var sender = new SqliteQueueSender(queue, dataSource, red.Name);
        await sender.SendAsync(envelope, TestContext.Current.CancellationToken);

        // Twenty polling intervals of the listener that used to leak
        await Task.Delay(2.Seconds(), TestContext.Current.CancellationToken);

        try
        {
            (await countAsync(dataSource, queueTable)).ShouldBe(1,
                "A queue listener on the shared tenant database is still polling after the host was stopped");
        }
        finally
        {
            // Leave nothing on the queue for the next run's listener to pick up at startup
            await executeAsync(dataSource, $"delete from {queueTable}");
        }
    }

    private static async Task executeAsync(SqliteDataSource dataSource, string sql)
    {
        await using var conn = await dataSource.OpenConnectionAsync();
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = sql;
        await cmd.ExecuteNonQueryAsync();
    }

    private static async Task<long> countAsync(SqliteDataSource dataSource, string table)
    {
        await using var conn = await dataSource.OpenConnectionAsync();
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = $"select count(*) from {table}";
        return Convert.ToInt64(await cmd.ExecuteScalarAsync());
    }
}

public record SharedPoolMessage(Guid Id);

public class SharedPoolMessageHandler
{
    public static void Handle(SharedPoolMessage message)
    {
        // Nothing to do. The test only cares whether anything is still pulling from the queue
    }
}
