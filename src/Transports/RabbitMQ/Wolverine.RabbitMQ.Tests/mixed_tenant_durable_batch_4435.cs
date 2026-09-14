using System.Collections.Concurrent;
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
using Wolverine.Persistence.Durability;
using Wolverine.Postgresql;
using Wolverine.Runtime;
using Wolverine.Tracking;
using Wolverine.Transports;
using Xunit;

namespace Wolverine.RabbitMQ.Tests;

/// <summary>
/// GH-4435. The reporter's exact shape: a durable RabbitMQ listener in front of database-per-tenant
/// message storage. Durable + the default MaximumMessagesToReceive (100) turns on the micro-batching
/// channel, so deliveries for DIFFERENT tenants are coalesced into one Envelope[] and handed to the
/// inbox as a single batch.
///
/// <para>
/// With one tenant's database unreachable, the old code posted that tenant's group to a RetryBlock,
/// which never rethrows. StoreIncomingAsync returned cleanly, DurableReceiver acked the whole batch,
/// and the unstored envelope went to the handler anyway -- then failed there and was discarded. The
/// broker showed an empty queue and no table held the message: unrecoverable loss, and it depended
/// only on whether that delivery happened to share a batch with another tenant's.
/// </para>
///
/// <para>
/// Two things are asserted here. The healthy tenant is unaffected and the listener keeps ACCEPTING --
/// one tenant database must not stop a listener serving every other tenant -- and the affected message
/// is still there to be handled once its database comes back.
/// </para>
/// </summary>
public class mixed_tenant_durable_batch_4435 : IAsyncLifetime
{
    private readonly string theSuffix = Guid.NewGuid().ToString("N")[..8];
    private readonly MixedTenantTracker theTracker = new();

    private IHost _host = null!;
    private string _queueName = null!;
    private WolverineRuntime _runtime = null!;

    private string MainSchema => $"mt4435_{theSuffix}";
    private string TenantDatabase => $"w4435_red_{theSuffix}";

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
                opts.ApplicationAssembly = typeof(mixed_tenant_durable_batch_4435).Assembly;

                opts.Services.AddSingleton(theTracker);

                opts.PersistMessagesWithPostgresql(Servers.PostgresConnectionString, MainSchema)
                    .RegisterStaticTenants(tenants => tenants.Register("red", ConnectionStringFor(TenantDatabase)));

                opts.UseRabbitMq().AutoProvision().AutoPurgeOnStartup().DisableDeadLetterQueueing();

                opts.PublishAllMessages().ToRabbitQueue(_queueName);

                // Durable + the default MaximumMessagesToReceive (100) is what builds the batching
                // channel, which is the code path this whole issue lives in.
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
    public async Task a_downed_tenant_does_not_take_the_batch_or_the_listener_down_with_it()
    {
        var redId = Guid.NewGuid();
        var mainId = Guid.NewGuid();

        // Stop consuming so both messages are sitting in the queue together. When the listener comes
        // back they are handed over in one batch, which is the only way to reach the mixed-tenant path.
        await theListeningAgent.StopAndDrainAsync();

        var bus = _host.MessageBus();
        await bus.PublishAsync(new MixedTenantMessage(redId), new DeliveryOptions { TenantId = "red" });
        await bus.PublishAsync(new MixedTenantMessage(mainId));

        // Let the publishes actually reach the broker before the listener is resumed
        await Task.Delay(1.Seconds(), TestContext.Current.CancellationToken);

        await dropTenantDatabaseAsync();

        await theListeningAgent.StartAsync();

        // The healthy tenant's message goes all the way through, even though it shared a batch with a
        // tenant whose database was gone.
        (await theTracker.WaitFor(mainId, 30.Seconds()))
            .ShouldBeTrue("the main tenant's message should still have been handled");

        // The listener serves every tenant, so one tenant's outage must not pause it. Before the fix
        // this either kept running while LOSING the message, or paused everyone once the failure was
        // made to propagate -- neither is acceptable.
        theListeningAgent.Status.ShouldBe(ListeningStatus.Accepting);

        // And the affected message was never handled without an inbox row behind it
        theTracker.Handled.ShouldNotContain(redId);

        // Now bring the tenant database back. The delivery was deferred rather than acked, so the
        // broker still has it, and the next redelivery lands. The bug report's headline symptom was
        // exactly this NOT happening -- the message was gone for good.
        await createTenantDatabaseAsync();
        var tenantStore = await _runtime.Stores.MultiTenanted.Single().GetDatabaseAsync("red");
        await tenantStore.Admin.MigrateAsync();

        (await theTracker.WaitFor(redId, 60.Seconds()))
            .ShouldBeTrue("the tenant's message should be handled once its database is reachable again");
    }
}

public record MixedTenantMessage(Guid Id);

public class MixedTenantTracker
{
    private readonly ConcurrentDictionary<Guid, TaskCompletionSource> _waiters = new();

    public ConcurrentBag<Guid> Handled { get; } = new();

    public void Record(Guid id)
    {
        Handled.Add(id);
        waiterFor(id).TrySetResult();
    }

    public async Task<bool> WaitFor(Guid id, TimeSpan timeout)
    {
        try
        {
            await waiterFor(id).Task.WaitAsync(timeout);
            return true;
        }
        catch (TimeoutException)
        {
            return false;
        }
    }

    private TaskCompletionSource waiterFor(Guid id)
    {
        return _waiters.GetOrAdd(id, _ => new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously));
    }
}

// Deliberately not a static class: Discovery.IncludeType<T>() cannot take one as a type argument.
public class MixedTenantMessageHandler
{
    public static void Handle(MixedTenantMessage message, MixedTenantTracker tracker)
    {
        tracker.Record(message.Id);
    }
}
