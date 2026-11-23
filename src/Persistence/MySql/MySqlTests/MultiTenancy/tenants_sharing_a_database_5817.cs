using IntegrationTests;
using JasperFx.Core;
using JasperFx.Core.Reflection;
using JasperFx.Resources;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using MySqlConnector;
using Shouldly;
using Wolverine;
using Wolverine.MySql;
using Wolverine.MySql.Transport;
using Wolverine.Persistence.Durability;
using Wolverine.Runtime;
using Wolverine.Tracking;
using Wolverine.Util;
using Xunit;

namespace MySqlTests.MultiTenancy;

/// <summary>
/// GH-5817, the MySQL twin of the SQL Server test of the same name. Two tenant ids registered against the SAME
/// database each get their own tenant message store, and both stores answer to the same name (the database
/// URI). <see cref="MultiTenantedQueueListener"/> started one queue listener per store at startup but kept them
/// in a map keyed by that name, so stop/dispose only ever reached one of them. The other kept polling the
/// shared database's queue table after the host was stopped and disposed.
/// </summary>
[Collection("mysql")]
public class tenants_sharing_a_database_5817 : MySqlMultiTenancyContext
{
    private const string QueueName = "sharedpool5817";
    private const string SchemaName = "tq5817";

    // A MySQL schema is a database, so each data source resolves its queue table inside its own database
    private const string QueueTableName = $"wolverine_queue_{QueueName}";

    protected override void configureWolverine(WolverineOptions opts)
    {
        opts.PersistMessagesWithMySql(Servers.MySqlConnectionString, SchemaName)
            .EnableMessageTransport()
            .RegisterStaticTenants(tenants =>
            {
                // "red" and "blue" share one pool database
                tenants.Register("red", tenant1ConnectionString);
                tenants.Register("blue", tenant1ConnectionString);
                tenants.Register("green", tenant2ConnectionString);
            });

        opts.ListenToMySqlQueue(QueueName).PollingInterval(100.Milliseconds());

        opts.Services.AddResourceSetupOnStartup();
        opts.Discovery.DisableConventionalDiscovery().IncludeType<SharedPoolMessageHandler>();
    }

    [Fact]
    public async Task no_queue_listener_outlives_the_host_when_two_tenants_share_a_database()
    {
        var runtime = theHost.GetRuntime();
        var queue = runtime.Options.Transports.OfType<MySqlTransport>().Single().Queues[QueueName];

        var stores = runtime.Stores.Main.As<MultiTenantedMessageStore>();
        var red = await stores.Source.FindAsync("red");
        var blue = await stores.Source.FindAsync("blue");
        red.Name.ShouldBe(blue.Name);

        runtime.Endpoints.FindListeningAgent(MySqlQueue.ToUri(QueueName, null))
            .ShouldNotBeNull();

        await theHost.StopAsync(TestContext.Current.CancellationToken);

        await using var dataSource = new MySqlDataSource(tenant1ConnectionString);
        await executeAsync(dataSource, $"delete from {QueueTableName}");

        // Nothing is listening any more, so a message put on the shared database's queue has to stay there
        var envelope = new Envelope(new SharedPoolMessage(Guid.NewGuid()))
        {
            Id = Guid.NewGuid(),
            ContentType = runtime.Options.DefaultSerializer.ContentType,
            MessageType = typeof(SharedPoolMessage).ToMessageTypeName(),
            Destination = MySqlQueue.ToUri(QueueName, red.Name)
        };
        envelope.Data = runtime.Options.DefaultSerializer.Write(envelope);

        var sender = new MySqlQueueSender(queue, dataSource, red.Name);
        await sender.SendAsync(envelope, TestContext.Current.CancellationToken);

        // Twenty polling intervals of the listener that used to leak
        await Task.Delay(2.Seconds(), TestContext.Current.CancellationToken);

        try
        {
            (await countAsync(dataSource, QueueTableName)).ShouldBe(1,
                "A queue listener on the shared tenant database is still polling after the host was stopped");
        }
        finally
        {
            // Leave nothing on the queue for the next run's listener to pick up at startup
            await executeAsync(dataSource, $"delete from {QueueTableName}");
        }
    }

    private static async Task executeAsync(MySqlDataSource dataSource, string sql)
    {
        await using var conn = await dataSource.OpenConnectionAsync();
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = sql;
        await cmd.ExecuteNonQueryAsync();
    }

    private static async Task<long> countAsync(MySqlDataSource dataSource, string table)
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
