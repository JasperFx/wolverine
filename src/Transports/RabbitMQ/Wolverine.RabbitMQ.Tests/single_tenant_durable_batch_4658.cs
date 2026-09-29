using IntegrationTests;
using JasperFx;
using JasperFx.Core;
using JasperFx.Resources;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Npgsql;
using Shouldly;
using Weasel.Postgresql;
using Weasel.Postgresql.Migrations;
using Wolverine.Logging;
using Wolverine.Postgresql;
using Wolverine.Runtime;
using Wolverine.Tracking;
using Wolverine.Transports;
using Xunit;

namespace Wolverine.RabbitMQ.Tests;

/// <summary>
/// GH-4658, the hole GH-4435 left. Same setup as <see cref="mixed_tenant_durable_batch_4435" /> -- a
/// durable RabbitMQ listener in front of database-per-tenant storage -- but the downed tenant's delivery
/// arrives ALONE, so the batch resolves to a single store and takes MultiTenantedMessageStore's
/// single-group fast path.
///
/// <para>
/// That path let the store's exception through raw, and a raw exception misses DurableReceiver's
/// IncludesMainStore check, so the listener paused for EVERY tenant. Worse, the pause then lasted the
/// whole outage: the resume probe ran ReleaseIncomingAsync across every store including the downed one.
/// </para>
///
/// <para>
/// This is the common shape rather than an edge. A stranded tenant's message usually arrives on its own,
/// and so does every redelivery of a deferred one -- which made whether a tenant outage stopped everyone
/// else depend on nothing but batch composition.
/// </para>
/// </summary>
public class single_tenant_durable_batch_4658 : IAsyncLifetime
{
    private readonly string theSuffix = Guid.NewGuid().ToString("N")[..8];

    // Reused from the GH-4435 test alongside this one; it is just a Guid-keyed "was this handled" latch.
    private readonly MixedTenantTracker theTracker = new();

    private IHost _host = null!;
    private string _queueName = null!;
    private WolverineRuntime _runtime = null!;

    private string MainSchema => $"mt4658_{theSuffix}";
    private string TenantDatabase => $"w4658_red_{theSuffix}";

    private static string ConnectionStringFor(string database)
    {
        return new NpgsqlConnectionStringBuilder(Servers.PostgresConnectionString)
        {
            Database = database
        }.ConnectionString;
    }

    public async ValueTask InitializeAsync()
    {
        _queueName = RabbitTesting.NextQueueName();

        await createTenantDatabaseAsync();

        _host = await Host.CreateDefaultBuilder()
            .UseWolverine(opts =>
            {
                opts.Durability.Mode = DurabilityMode.Solo;
                opts.ApplicationAssembly = typeof(single_tenant_durable_batch_4658).Assembly;

                opts.Services.AddSingleton(theTracker);

                opts.PersistMessagesWithPostgresql(Servers.PostgresConnectionString, MainSchema)
                    .RegisterStaticTenants(tenants => tenants.Register("red", ConnectionStringFor(TenantDatabase)));

                opts.UseRabbitMq().AutoProvision().AutoPurgeOnStartup().DisableDeadLetterQueueing();

                opts.PublishAllMessages().ToRabbitQueue(_queueName);

                // Durable + the default MaximumMessagesToReceive (100) is the batching channel. The point
                // here is that a batch of ONE still goes through it.
                opts.ListenToRabbitQueue(_queueName).UseDurableInbox();

                opts.Discovery.DisableConventionalDiscovery().IncludeType<MixedTenantMessageHandler>();

                opts.Services.AddResourceSetupOnStartup();
            }).StartAsync();

        _runtime = _host.GetRuntime();
    }

    public async ValueTask DisposeAsync()
    {
        await _host.StopAsync();
        _host.Dispose();

        await dropTenantDatabaseAsync();
    }

    private async Task createTenantDatabaseAsync()
    {
        await using var conn = new NpgsqlConnection(Servers.PostgresConnectionString);
        await conn.OpenAsync();

        if (!await conn.DatabaseExists(TenantDatabase))
        {
            await new DatabaseSpecification().BuildDatabase(conn, TenantDatabase);
        }

        await conn.CloseAsync();
    }

    private async Task dropTenantDatabaseAsync()
    {
        await using var conn = new NpgsqlConnection(Servers.PostgresConnectionString);
        await conn.OpenAsync();

        // FORCE terminates the pooled connections the tenant store is holding open
        await using var drop = new NpgsqlCommand($"DROP DATABASE IF EXISTS {TenantDatabase} WITH (FORCE)", conn);
        await drop.ExecuteNonQueryAsync();

        await conn.CloseAsync();
    }

    private IListeningAgent theListeningAgent =>
        _runtime.Endpoints.FindListeningAgent(new Uri($"rabbitmq://queue/{_queueName}"))!;

    [Fact]
    public async Task a_batch_belonging_to_one_downed_tenant_does_not_pause_the_listener()
    {
        var redId = Guid.NewGuid();
        var mainId = Guid.NewGuid();

        // Stop consuming so the tenant's message is waiting in the queue, ALONE. When the listener comes
        // back it is handed over as a batch of one, which resolves to a single store -- the fast path.
        await theListeningAgent.StopAndDrainAsync();

        var bus = _host.MessageBus();
        await bus.PublishAsync(new MixedTenantMessage(redId), new DeliveryOptions { TenantId = "red" });

        // Let the publish actually reach the broker before the listener is resumed
        await Task.Delay(1.Seconds(), TestContext.Current.CancellationToken);

        await dropTenantDatabaseAsync();

        // Subscribed only now, so the deliberate StopAndDrainAsync above is not counted. Asserting on the
        // pause ITSELF and not just on the delivery matters: the GH-4658 probe fix restarts a wrongly
        // paused listener within a couple of seconds, which is fast enough to hide the wrong pause behind
        // a delivery assertion alone.
        var pauses = new ListenerPauseCounter(theListeningAgent.Endpoint.Uri);
        using var subscription = _runtime.Tracker.Subscribe(pauses);

        await theListeningAgent.StartAsync();

        // Long enough for the failed batch to have signalled a pause, which it did in the same
        // millisecond as the write failure on the old code.
        await Task.Delay(1.Seconds(), TestContext.Current.CancellationToken);

        // THE assertion. Published after the tenant-only failure, so on the old code the listener was
        // already paused and this message sat in the queue for the whole of the tenant's outage.
        await bus.PublishAsync(new MixedTenantMessage(mainId));

        (await theTracker.WaitFor(mainId, 30.Seconds()))
            .ShouldBeTrue("a message for a healthy store must still be handled while one tenant is down");

        theListeningAgent.Status.ShouldBe(ListeningStatus.Accepting);

        // One tenant's database being down is not a reason to stop a listener that serves every tenant,
        // not even briefly.
        pauses.Count.ShouldBe(0, "a tenant-only inbox failure must not pause the listener at all");

        // And the tenant's own message was deferred rather than acked, so it is still at the broker and
        // lands once its database is reachable again.
        theTracker.Handled.ShouldNotContain(redId);

        await createTenantDatabaseAsync();
        var tenantStore = await _runtime.Stores.MultiTenanted.Single().GetDatabaseAsync("red");
        await tenantStore.Admin.MigrateAsync();

        (await theTracker.WaitFor(redId, 60.Seconds()))
            .ShouldBeTrue("the tenant's message should be handled once its database is reachable again");
    }
}

/// <summary>
/// Counts the times one listener was stopped. PauseForInboxRecoveryAsync publishes Stopped, so this only
/// means something for a window in which nothing stops the listener deliberately.
/// </summary>
internal class ListenerPauseCounter(Uri uri) : IObserver<IWolverineEvent>
{
    public int Count { get; private set; }

    public void OnNext(IWolverineEvent value)
    {
        if (value is ListenerState state && state.Uri == uri &&
            state.Status is ListeningStatus.Stopped or ListeningStatus.Paused)
        {
            Count++;
        }
    }

    public void OnCompleted()
    {
    }

    public void OnError(Exception error)
    {
    }
}
