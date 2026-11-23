using IntegrationTests;
using JasperFx.Core;
using JasperFx.Core.Reflection;
using JasperFx.Resources;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Npgsql;
using Shouldly;
using Wolverine;
using Wolverine.Persistence.Durability;
using Wolverine.Postgresql;
using Wolverine.Postgresql.Transport;
using Wolverine.Runtime;
using Wolverine.Tracking;
using Wolverine.Util;
using Xunit;

namespace PostgresqlTests.MultiTenancy;

/// <summary>
/// GH-5817, the PostgreSQL twin of the SQL Server test of the same name. Two tenant ids registered against the
/// SAME database each get their own tenant message store, and both stores answer to the same name (the
/// database URI). <see cref="MultiTenantedQueueListener"/> started one queue listener per store at startup but
/// kept them in a map keyed by that name, so stop/dispose only ever reached one of them. The other kept polling
/// the shared database's queue table after the host was stopped and disposed.
/// </summary>
public class tenants_sharing_a_database_5817 : MultiTenancyContext
{
    private const string QueueName = "sharedpool5817";
    private const string SchemaName = "tq5817";

    protected override void configureWolverine(WolverineOptions opts)
    {
        opts.PersistMessagesWithPostgresql(Servers.PostgresConnectionString, SchemaName)
            .EnableMessageTransport(transport => transport.TransportSchemaName(SchemaName).AutoProvision())
            .RegisterStaticTenants(tenants =>
            {
                // "red" and "blue" share one pool database
                tenants.Register("red", tenant1ConnectionString);
                tenants.Register("blue", tenant1ConnectionString);
                tenants.Register("green", tenant2ConnectionString);
            });

        opts.ListenToPostgresqlQueue(QueueName).PollingInterval(100.Milliseconds());

        opts.Services.AddResourceSetupOnStartup();
        opts.Discovery.DisableConventionalDiscovery().IncludeType<SharedPoolMessageHandler>();
    }

    [Fact]
    public async Task no_queue_listener_outlives_the_host_when_two_tenants_share_a_database()
    {
        var runtime = theHost.GetRuntime();
        var queue = runtime.Options.Transports.OfType<PostgresqlTransport>().Single().Queues[QueueName];

        var stores = runtime.Stores.Main.As<MultiTenantedMessageStore>();
        var red = await stores.Source.FindAsync("red");
        var blue = await stores.Source.FindAsync("blue");
        red.Name.ShouldBe(blue.Name);

        runtime.Endpoints.FindListeningAgent(PostgresqlQueue.ToUri(QueueName, null))
            .ShouldNotBeNull();

        await theHost.StopAsync(TestContext.Current.CancellationToken);

        await using var dataSource = NpgsqlDataSource.Create(tenant1ConnectionString);
        var queueTable = queue.QueueTable.Identifier.QualifiedName;
        await executeAsync(dataSource, $"delete from {queueTable}");

        // Nothing is listening any more, so a message put on the shared database's queue has to stay there
        var envelope = new Envelope(new SharedPoolMessage(Guid.NewGuid()))
        {
            Id = Guid.NewGuid(),
            ContentType = runtime.Options.DefaultSerializer.ContentType,
            MessageType = typeof(SharedPoolMessage).ToMessageTypeName(),
            Destination = PostgresqlQueue.ToUri(QueueName, red.Name)
        };
        envelope.Data = runtime.Options.DefaultSerializer.Write(envelope);

        var sender = new PostgresqlQueueSender(queue, dataSource, red.Name);
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

    private static async Task executeAsync(NpgsqlDataSource dataSource, string sql)
    {
        await using var conn = await dataSource.OpenConnectionAsync();
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = sql;
        await cmd.ExecuteNonQueryAsync();
    }

    private static async Task<long> countAsync(NpgsqlDataSource dataSource, string table)
    {
        await using var conn = await dataSource.OpenConnectionAsync();
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = $"select count(*) from {table}";
        return (long)(await cmd.ExecuteScalarAsync())!;
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
