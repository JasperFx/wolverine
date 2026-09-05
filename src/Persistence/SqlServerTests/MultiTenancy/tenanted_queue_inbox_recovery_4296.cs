using IntegrationTests;
using JasperFx;
using JasperFx.Core;
using JasperFx.Core.Reflection;
using JasperFx.Resources;
using Microsoft.Extensions.DependencyInjection;
using Shouldly;
using Wolverine;
using Wolverine.Persistence.Durability;
using Wolverine.Runtime;
using Wolverine.SqlServer;
using Wolverine.SqlServer.Transport;
using Wolverine.Tracking;
using Wolverine.Transports;
using Wolverine.Util;
using Xunit;

namespace SqlServerTests.MultiTenancy;

/// <summary>
/// GH-4296, the SQL Server twin of the PostgreSQL test of the same name. A database transport queue that is
/// multi-tenanted by database is served by one <see cref="MultiTenantedQueueListener"/> registered under the
/// bare "sqlserver://queue" address, while the per-database listeners underneath it stamp their own
/// "sqlserver://queue/database" address into received_at. Inbox recovery resolves rows back to a listener by
/// that address, so orphaned envelopes were addressed to a listener nothing had registered.
/// </summary>
public class tenanted_queue_inbox_recovery_4296 : MultiTenancyContext
{
    private const string QueueName = "heavy4296";
    private const string SchemaName = "tq4296";

    private readonly string[] theTenants = ["red", "blue", "green"];

    protected override void configureWolverine(WolverineOptions opts)
    {
        opts.Durability.Mode = DurabilityMode.Solo;
        opts.Durability.ScheduledJobPollingTime = 250.Milliseconds();

        opts.UseSqlServerPersistenceAndTransport(Servers.SqlServerConnectionString, SchemaName, SchemaName)
            .AutoProvision();

        opts.PersistMessagesWithSqlServer(Servers.SqlServerConnectionString, SchemaName)
            .RegisterStaticTenants(tenants =>
            {
                tenants.Register("red", tenant1ConnectionString);
                tenants.Register("blue", tenant2ConnectionString);
                tenants.Register("green", tenant3ConnectionString);
            });

        opts.Services.AddResourceSetupOnStartup();

        opts.Discovery.DisableConventionalDiscovery()
            .IncludeType<OrphanedSqlServerQueueMessageHandler>();

        opts.ListenToSqlServerQueue(QueueName);
    }

    [Fact]
    public async Task the_per_database_address_resolves_back_to_the_registered_listener()
    {
        var runtime = theHost.GetRuntime();
        var bare = SqlServerQueue.ToUri(QueueName, null);

        runtime.Endpoints.FindListeningAgent(bare)
            .ShouldNotBeNull("The multi-tenanted queue listener should be running in Solo mode");

        foreach (var store in await tenantStoresAsync(runtime))
        {
            var receivedAt = SqlServerQueue.ToUri(QueueName, store.Name);

            // Nothing is registered under the per-database address -- that is the whole defect
            runtime.Endpoints.FindListeningAgent(receivedAt).ShouldBeNull();

            var circuit = runtime.Endpoints.FindListenerCircuit(receivedAt);
            circuit.ShouldNotBeNull($"No listener circuit resolved for the per-database address {receivedAt}");
            circuit.Endpoint.Uri.ShouldBe(bare);
        }
    }

    [Fact]
    public async Task orphaned_envelopes_in_a_tenant_database_are_recovered()
    {
        using var tracking = OrphanedSqlServerQueueMessages.Track();

        var runtime = theHost.GetRuntime();

        var expected = new List<Guid>();
        foreach (var store in await tenantStoresAsync(runtime))
        {
            var receivedAt = SqlServerQueue.ToUri(QueueName, store.Name);
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
            var envelope = new Envelope(new OrphanedSqlServerQueueMessage(id, i))
            {
                Id = id,
                Destination = receivedAt,
                Status = EnvelopeStatus.Incoming,
                OwnerId = TransportConstants.AnyNode,
                ContentType = serializer.ContentType,
                MessageType = typeof(OrphanedSqlServerQueueMessage).ToMessageTypeName(),
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

public record OrphanedSqlServerQueueMessage(Guid Id, int Index);

public static class OrphanedSqlServerQueueMessages
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

public class OrphanedSqlServerQueueMessageHandler
{
    public static void Handle(OrphanedSqlServerQueueMessage message)
    {
        OrphanedSqlServerQueueMessages.Record(message.Id);
    }
}
