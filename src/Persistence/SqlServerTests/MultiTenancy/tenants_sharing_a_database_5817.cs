using IntegrationTests;
using JasperFx.Core;
using JasperFx.Core.Reflection;
using JasperFx.Resources;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Shouldly;
using Wolverine;
using Wolverine.Persistence.Durability;
using Wolverine.Runtime;
using Wolverine.SqlServer;
using Wolverine.SqlServer.Transport;
using Wolverine.Tracking;
using Wolverine.Util;
using Xunit;

namespace SqlServerTests.MultiTenancy;

/// <summary>
/// GH-5817. Two tenant ids registered against the SAME database each get their own tenant message store, and
/// both stores answer to the same name (the database URI). <see cref="MultiTenantedQueueListener"/> started one
/// queue listener per store at startup but kept them in a map keyed by that name, so the second listener
/// overwrote the first and stop/dispose only ever reached one of them. The other kept polling the shared
/// database's queue table after the host was stopped and disposed.
/// </summary>
public class tenants_sharing_a_database_5817 : MultiTenancyContext
{
    private const string QueueName = "sharedpool5817";
    private const string SchemaName = "tq5817";

    protected override void configureWolverine(WolverineOptions opts)
    {
        opts.UseSqlServerPersistenceAndTransport(Servers.SqlServerConnectionString, SchemaName, SchemaName)
            .AutoProvision();

        opts.PersistMessagesWithSqlServer(Servers.SqlServerConnectionString, SchemaName)
            .RegisterStaticTenants(tenants =>
            {
                // "red" and "blue" share one pool database
                tenants.Register("red", tenant1ConnectionString);
                tenants.Register("blue", tenant1ConnectionString);
                tenants.Register("green", tenant2ConnectionString);
            });

        opts.ListenToSqlServerQueue(QueueName).PollingInterval(100.Milliseconds());

        opts.Services.AddResourceSetupOnStartup();
        opts.Discovery.DisableConventionalDiscovery().IncludeType<SharedPoolMessageHandler>();
    }

    [Fact]
    public async Task no_queue_listener_outlives_the_host_when_two_tenants_share_a_database()
    {
        var runtime = theHost.GetRuntime();
        var queue = runtime.Options.Transports.OfType<SqlServerTransport>().Single().Queues[QueueName];

        var stores = runtime.Stores.Main.As<MultiTenantedMessageStore>();
        var red = await stores.Source.FindAsync("red");
        var blue = await stores.Source.FindAsync("blue");
        red.Name.ShouldBe(blue.Name);

        runtime.Endpoints.FindListeningAgent(SqlServerQueue.ToUri(QueueName, null))
            .ShouldNotBeNull();

        await theHost.StopAsync(TestContext.Current.CancellationToken);

        var queueTable = queue.QueueTable.Identifier.QualifiedName;
        await clearAsync(tenant1ConnectionString, queueTable);

        // Nothing is listening any more, so a message put on the shared database's queue has to stay there
        var envelope = new Envelope(new SharedPoolMessage(Guid.NewGuid()))
        {
            Id = Guid.NewGuid(),
            ContentType = runtime.Options.DefaultSerializer.ContentType,
            MessageType = typeof(SharedPoolMessage).ToMessageTypeName(),
            Destination = SqlServerQueue.ToUri(QueueName, red.Name)
        };
        envelope.Data = runtime.Options.DefaultSerializer.Write(envelope);

        var sender = new SqlServerQueueSender(queue, tenant1ConnectionString, red.Name);
        await sender.SendAsync(envelope, TestContext.Current.CancellationToken);

        // Twenty polling intervals of the listener that used to leak
        await Task.Delay(2.Seconds(), TestContext.Current.CancellationToken);

        try
        {
            (await countAsync(tenant1ConnectionString, queueTable)).ShouldBe(1,
                "A queue listener on the shared tenant database is still polling after the host was stopped");
        }
        finally
        {
            // Leave nothing on the queue for the next run's listener to pick up at startup
            await clearAsync(tenant1ConnectionString, queueTable);
        }
    }

    private static async Task clearAsync(string connectionString, string table)
    {
        await using var conn = new SqlConnection(connectionString);
        await conn.OpenAsync();
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = $"delete from {table}";
        await cmd.ExecuteNonQueryAsync();
    }

    private static async Task<int> countAsync(string connectionString, string table)
    {
        await using var conn = new SqlConnection(connectionString);
        await conn.OpenAsync();
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = $"select count(*) from {table}";
        return (int)(await cmd.ExecuteScalarAsync())!;
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
