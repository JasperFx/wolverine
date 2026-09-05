using IntegrationTests;
using JasperFx;
using JasperFx.Core;
using JasperFx.Core.Reflection;
using JasperFx.Resources;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Npgsql;
using Shouldly;
using Weasel.Postgresql;
using Weasel.Postgresql.Migrations;
using Wolverine;
using Wolverine.Persistence.Durability;
using Wolverine.Postgresql;
using Wolverine.Postgresql.Transport;
using Wolverine.Runtime;
using Wolverine.Tracking;
using Wolverine.Transports;
using Wolverine.Util;
using Xunit;

namespace PostgresqlTests.MultiTenancy;

/// <summary>
/// GH-4296. A PostgreSQL transport queue that is multi-tenanted by database is served by a single
/// <see cref="MultiTenantedQueueListener"/>, registered under the bare "postgresql://queue" address. The
/// per-database listeners it owns each stamp their OWN, more specific "postgresql://queue/database" address
/// onto everything they receive, and that is what lands in received_at.
///
/// Inbox recovery groups orphaned rows by received_at and then asks the endpoint collection for the listener
/// they belong to, so those rows were addressed to a listener that had never been registered under that name.
/// The lookup missed, and the envelopes of any node that died mid-flight sat in the tenant database as
/// owner_id = 0 forever -- not eventually, ever.
/// </summary>
public class tenanted_queue_inbox_recovery_4296 : PostgresqlContext, IAsyncLifetime
{
    private const string QueueName = "heavy4296";

    private readonly string theSuffix = Guid.NewGuid().ToString("N")[..8];
    private readonly string[] theTenants = ["red", "blue", "green"];

    private IHost _host = null!;

    private string MainSchema => $"tq4296_{theSuffix}";
    private string TenantDatabase(string tenant) => $"w4296_{tenant}_{theSuffix}";

    private static string ConnectionStringFor(string database)
    {
        return new NpgsqlConnectionStringBuilder(Servers.PostgresConnectionString)
        {
            Database = database
        }.ConnectionString;
    }

    public async ValueTask InitializeAsync()
    {
        await using (var conn = new NpgsqlConnection(Servers.PostgresConnectionString))
        {
            await conn.OpenAsync();

            foreach (var tenant in theTenants)
            {
                var databaseName = TenantDatabase(tenant);
                if (!await conn.DatabaseExists(databaseName))
                {
                    await new DatabaseSpecification().BuildDatabase(conn, databaseName);
                }
            }

            await conn.CloseAsync();
        }

        _host = await Host.CreateDefaultBuilder()
            .UseWolverine(opts =>
            {
                opts.Durability.Mode = DurabilityMode.Solo;
                opts.Durability.ScheduledJobPollingTime = 250.Milliseconds();

                opts.PersistMessagesWithPostgresql(ConnectionStringFor("postgres"), MainSchema)
                    .EnableMessageTransport(transport => transport.TransportSchemaName(MainSchema))
                    .RegisterStaticTenants(tenants =>
                    {
                        foreach (var tenant in theTenants)
                        {
                            tenants.Register(tenant, ConnectionStringFor(TenantDatabase(tenant)));
                        }
                    });

                opts.Services.AddResourceSetupOnStartup();

                opts.Discovery.DisableConventionalDiscovery()
                    .IncludeType<OrphanedQueueMessageHandler>();

                opts.ListenToPostgresqlQueue(QueueName);
            }).StartAsync();
    }

    public async ValueTask DisposeAsync()
    {
        await _host.StopAsync();
        _host.Dispose();
    }

    [Fact]
    public async Task the_per_database_address_resolves_back_to_the_registered_listener()
    {
        var runtime = _host.GetRuntime();
        var bare = PostgresqlQueue.ToUri(QueueName, null);

        // This is the address the listening agent is actually registered under
        runtime.Endpoints.FindListeningAgent(bare)
            .ShouldNotBeNull("The multi-tenanted queue listener should be running in Solo mode");

        foreach (var store in await tenantStoresAsync(runtime))
        {
            var receivedAt = PostgresqlQueue.ToUri(QueueName, store.Name);

            // Nothing is registered under the per-database address -- that is the whole defect
            runtime.Endpoints.FindListeningAgent(receivedAt).ShouldBeNull();

            // ...but recovery still has to be able to find the circuit these rows belong to
            var circuit = runtime.Endpoints.FindListenerCircuit(receivedAt);
            circuit.ShouldNotBeNull($"No listener circuit resolved for the per-database address {receivedAt}");
            circuit.Endpoint.Uri.ShouldBe(bare);
        }
    }

    [Fact]
    public async Task orphaned_envelopes_in_a_tenant_database_are_recovered()
    {
        using var tracking = OrphanedQueueMessages.Track();

        var runtime = _host.GetRuntime();

        var expected = new List<Guid>();
        foreach (var store in await tenantStoresAsync(runtime))
        {
            // received_at exactly as the per-database listener would have stamped it before its node died
            var receivedAt = PostgresqlQueue.ToUri(QueueName, store.Name);
            expected.AddRange(await seedAsync(store, runtime, receivedAt, 3));
        }

        var succeeded = await waitForAsync(() => expected.All(tracking.Contains), 60.Seconds());

        succeeded.ShouldBeTrue(
            $"Expected all {expected.Count} orphaned envelopes across {theTenants.Length} tenant databases to be " +
            $"recovered through the multi-tenanted queue listener, but only saw {tracking.Count}");
    }

    private async Task<IReadOnlyList<IMessageStore>> tenantStoresAsync(IWolverineRuntime runtime)
    {
        var stores = runtime.Stores.Main.As<MultiTenantedMessageStore>();
        var list = new List<IMessageStore>();
        foreach (var tenant in theTenants)
        {
            var store = await stores.Source.FindAsync(tenant);
            store.ShouldNotBeNull();
            list.Add(store!);
        }

        return list;
    }

    private static async Task<Guid[]> seedAsync(IMessageStore store, IWolverineRuntime runtime, Uri receivedAt,
        int count)
    {
        var serializer = runtime.Options.DefaultSerializer;

        var envelopes = Enumerable.Range(0, count).Select(i =>
        {
            var id = Guid.NewGuid();
            var envelope = new Envelope(new OrphanedQueueMessage(id, i))
            {
                Id = id,
                Destination = receivedAt,
                Status = EnvelopeStatus.Incoming,
                OwnerId = TransportConstants.AnyNode,
                ContentType = serializer.ContentType,
                MessageType = typeof(OrphanedQueueMessage).ToMessageTypeName(),
                SentAt = DateTimeOffset.UtcNow
            };

            envelope.Data = serializer.Write(envelope);

            return envelope;
        }).ToArray();

        await store.Inbox.StoreIncomingAsync(envelopes);

        return envelopes.Select(x => x.Id).ToArray();
    }

    private static async Task<bool> waitForAsync(Func<bool> condition, TimeSpan timeout)
    {
        var stopwatch = System.Diagnostics.Stopwatch.StartNew();
        while (stopwatch.Elapsed < timeout)
        {
            if (condition()) return true;
            await Task.Delay(100.Milliseconds());
        }

        return condition();
    }
}

public record OrphanedQueueMessage(Guid Id, int Index);

public static class OrphanedQueueMessages
{
    private static readonly object _lock = new();
    private static HashSet<Guid>? _received;

    public static Tracker Track()
    {
        lock (_lock)
        {
            _received = new HashSet<Guid>();
        }

        return new Tracker();
    }

    public static void Record(Guid id)
    {
        lock (_lock)
        {
            _received?.Add(id);
        }
    }

    public sealed class Tracker : IDisposable
    {
        public bool Contains(Guid id)
        {
            lock (_lock)
            {
                return _received?.Contains(id) ?? false;
            }
        }

        public int Count
        {
            get
            {
                lock (_lock)
                {
                    return _received?.Count ?? 0;
                }
            }
        }

        public void Dispose()
        {
            lock (_lock)
            {
                _received = null;
            }
        }
    }
}

public class OrphanedQueueMessageHandler
{
    public static void Handle(OrphanedQueueMessage message)
    {
        OrphanedQueueMessages.Record(message.Id);
    }
}
