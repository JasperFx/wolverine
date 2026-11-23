using IntegrationTests;
using JasperFx.Core;
using JasperFx.Core.Reflection;
using Microsoft.Extensions.Hosting;
using Shouldly;
using Weasel.Oracle;
using Wolverine;
using Wolverine.Oracle;
using Wolverine.Oracle.Transport;
using Wolverine.Persistence.Durability;
using Wolverine.Runtime;
using Wolverine.Tracking;
using Wolverine.Util;
using Xunit;

namespace OracleTests.Transport;

/// <summary>
/// GH-5817, the Oracle twin of the SQL Server test of the same name. Two tenant ids registered against the SAME
/// database each get their own tenant message store, and both stores answer to the same name (the database
/// URI). <see cref="MultiTenantedQueueListener"/> started one queue listener per store at startup but kept them
/// in a map keyed by that name, so stop/dispose only ever reached one of them. The other kept polling the
/// shared database's queue table after the host was stopped.
/// </summary>
[Collection("oracle")]
public class tenants_sharing_a_database_5817 : IAsyncLifetime
{
    private const string QueueName = "sharedpool5817";

    private IHost _host = null!;

    public async ValueTask InitializeAsync()
    {
        _host = await Host.CreateDefaultBuilder()
            .UseWolverine(opts =>
            {
                opts.Durability.Mode = DurabilityMode.Solo;

                // There is one Oracle database to test against, so both tenants share it. That is the case
                // this test is about
                opts.PersistMessagesWithOracle(Servers.OracleConnectionString, "WOLVERINE")
                    .RegisterStaticTenants(tenants =>
                    {
                        tenants.Register("red", Servers.OracleConnectionString);
                        tenants.Register("blue", Servers.OracleConnectionString);
                    })
                    .EnableMessageTransport(x => x.AutoProvision());

                opts.ListenToOracleQueue(QueueName).PollingInterval(100.Milliseconds());

                opts.Discovery.DisableConventionalDiscovery().IncludeType<SharedPoolMessageHandler>();
            }).StartAsync();
    }

    public async ValueTask DisposeAsync()
    {
        await _host.StopAsync();
        _host.Dispose();
    }

    [Fact]
    public async Task no_queue_listener_outlives_the_host_when_two_tenants_share_a_database()
    {
        var runtime = _host.GetRuntime();
        var queue = runtime.Options.Transports.OfType<OracleTransport>().Single().Queues[QueueName];

        var stores = runtime.Stores.Main.As<MultiTenantedMessageStore>();
        var red = await stores.Source.FindAsync("red");
        var blue = await stores.Source.FindAsync("blue");
        red.Name.ShouldBe(blue.Name);

        runtime.Endpoints.FindListeningAgent(OracleQueue.ToUri(QueueName, null))
            .ShouldNotBeNull();

        await _host.StopAsync(TestContext.Current.CancellationToken);

        var dataSource = new OracleDataSource(Servers.OracleConnectionString);
        var queueTable = queue.QueueTable.Identifier.QualifiedName;
        await executeAsync(dataSource, $"DELETE FROM {queueTable}");

        // Nothing is listening any more, so a message put on the shared database's queue has to stay there
        var envelope = new Envelope(new SharedPoolMessage(Guid.NewGuid()))
        {
            Id = Guid.NewGuid(),
            ContentType = runtime.Options.DefaultSerializer.ContentType,
            MessageType = typeof(SharedPoolMessage).ToMessageTypeName(),
            Destination = OracleQueue.ToUri(QueueName, red.Name)
        };
        envelope.Data = runtime.Options.DefaultSerializer.Write(envelope);

        var sender = new OracleQueueSender(queue, dataSource, red.Name);
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
            await executeAsync(dataSource, $"DELETE FROM {queueTable}");
        }
    }

    private static async Task executeAsync(OracleDataSource dataSource, string sql)
    {
        await using var conn = await dataSource.OpenConnectionAsync();
        await using var cmd = conn.CreateCommand(sql);
        await cmd.ExecuteNonQueryAsync();
    }

    private static async Task<long> countAsync(OracleDataSource dataSource, string table)
    {
        await using var conn = await dataSource.OpenConnectionAsync();
        await using var cmd = conn.CreateCommand($"SELECT COUNT(*) FROM {table}");
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
