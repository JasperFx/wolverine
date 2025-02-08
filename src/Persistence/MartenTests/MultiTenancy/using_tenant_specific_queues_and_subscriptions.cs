using IntegrationTests;
using JasperFx.Core;
using Marten;
using Marten.Events.Daemon;
using Marten.Events.Daemon.Internals;
using Marten.Events.Daemon.Resiliency;
using Marten.Schema;
using Marten.Storage;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Npgsql;
using JasperFx.Resources;
using Weasel.Postgresql;
using Weasel.Postgresql.Migrations;
using Wolverine;
using Wolverine.Marten;
using Wolverine.Marten.Subscriptions;
using Wolverine.Postgresql;
using Wolverine.Postgresql.Transport;
using Wolverine.Tracking;

namespace MartenTests.MultiTenancy;

public class using_tenant_specific_queues_and_subscriptions : PostgresqlContext, IAsyncLifetime
{
    private readonly List<IHost> _receivers = new();
    private IHost _sender;
    private string tenant1ConnectionString;
    private string tenant2ConnectionString;
    private string tenant3ConnectionString;
    private string tenant4ConnectionString;
    private IDocumentStore theSenderStore;

    public async Task InitializeAsync()
    {
        await using var conn = new NpgsqlConnection(Servers.PostgresConnectionString);
        await conn.OpenAsync();

        await conn.DropSchemaAsync("tenants");

        tenant1ConnectionString = await CreateDatabaseIfNotExists(conn, "tenant1");
        tenant2ConnectionString = await CreateDatabaseIfNotExists(conn, "tenant2");
        tenant3ConnectionString = await CreateDatabaseIfNotExists(conn, "tenant3");
        tenant4ConnectionString = await CreateDatabaseIfNotExists(conn, "tenant4");

        await conn.CloseAsync();

        // Start with a pair of tenants
        var tenancy = new MasterTableTenancy(new StoreOptions { DatabaseSchemaName = "tenants" },
            Servers.PostgresConnectionString, "tenants");
        await tenancy.ClearAllDatabaseRecordsAsync();
        await tenancy.AddDatabaseRecordAsync("tenant1", tenant1ConnectionString);
        await tenancy.AddDatabaseRecordAsync("tenant2", tenant2ConnectionString);
        await tenancy.AddDatabaseRecordAsync("tenant3", tenant3ConnectionString);
        await tenancy.AddDatabaseRecordAsync("tenant4", tenant4ConnectionString);

        await cleanOldData(tenant1ConnectionString);
        await cleanOldData(tenant2ConnectionString);
        await cleanOldData(tenant3ConnectionString);
        await cleanOldData(tenant4ConnectionString);

        _sender = await Host.CreateDefaultBuilder()
            .UseWolverine(opts =>
            {
                // This is too extreme for real usage, but helps tests to run faster
                opts.Durability.NodeReassignmentPollingTime = 1.Seconds();
                opts.Durability.HealthCheckPollingTime = 1.Seconds();

                opts.Durability.Mode = DurabilityMode.Solo;

                opts.PublishMessage<UpdateColorCounts>().ToPostgresqlQueue("numbers");

                opts.Services.AddMarten(o =>
                    {
                        o.DatabaseSchemaName = "color_sender";

                        o.Schema.For<ColorUpdates>().DatabaseSchemaName("colors");
                        o.DisableNpgsqlLogging = true;

                        // This is a new strategy for configuring tenant databases with Marten
                        // In this usage, Marten is tracking the tenant databases in a single table in the "master"
                        // database by tenant
                        o.MultiTenantedDatabasesWithMasterDatabaseTable(Servers.PostgresConnectionString, "tenants");
                    })
                    .IntegrateWithWolverine(m =>
                    {
                        m.MessageStorageSchemaName = "mt";
                        m.MasterDatabaseConnectionString = Servers.PostgresConnectionString;
                    })
                    .SubscribeToEvents(new ColorsSubscription())
                    .AddAsyncDaemon(DaemonMode.Solo)

                    // All detected changes will be applied to all
                    // the configured tenant databases on startup
                    .ApplyAllDatabaseChangesOnStartup();

                opts.Services.AddResourceSetupOnStartup();
            })
            .StartAsync();

        theSenderStore = _sender.Services.GetRequiredService<IDocumentStore>();

    }

    public async Task DisposeAsync()
    {
        foreach (var host in _receivers)
        {
            host.GetRuntime().Agents.DisableHealthChecks();
        }

        _receivers.Reverse();
        foreach (var host in _receivers.ToArray())
        {
            await shutdownHostAsync(host);
        }

        await _sender.StopAsync();
        _sender.Dispose();
    }

    private async Task shutdownHostAsync(IHost host)
    {
        host.GetRuntime().Agents.DisableHealthChecks();
        await host.StopAsync();
        host.Dispose();
        _receivers.Remove(host);
    }

    private async Task<string> CreateDatabaseIfNotExists(NpgsqlConnection conn, string databaseName)
    {
        var builder = new NpgsqlConnectionStringBuilder(Servers.PostgresConnectionString);

        var exists = await conn.DatabaseExists(databaseName);
        if (!exists)
        {
            await new DatabaseSpecification().BuildDatabase(conn, databaseName);
        }

        builder.Database = databaseName;

        return builder.ConnectionString;
    }

    protected async Task<IHost> startNewReceiver()
    {
        // Setting up a Host with Multi-tenancy
        var host = await Host.CreateDefaultBuilder()
            .UseWolverine(opts =>
            {
                opts.Durability.Mode = DurabilityMode.Balanced;

                opts.ListenToPostgresqlQueue("numbers").ListenWithStrictOrdering();

                opts.Transports.GetOrCreate<PostgresqlTransport>().AutoProvision = true;

                opts.Services.AddMarten(o =>
                    {
                        o.DisableNpgsqlLogging = true;

                        o.DatabaseSchemaName = "colors";

                        // This is a new strategy for configuring tenant databases with Marten
                        // In this usage, Marten is tracking the tenant databases in a single table in the "master"
                        // database by tenant
                        o.MultiTenantedDatabasesWithMasterDatabaseTable(Servers.PostgresConnectionString, "tenants");
                    })
                    .IntegrateWithWolverine(m =>
                    {
                        m.MessageStorageSchemaName = "mt_queues";
                        m.MasterDatabaseConnectionString = Servers.PostgresConnectionString;
                    })

                    // All detected changes will be applied to all
                    // the configured tenant databases on startup
                    .ApplyAllDatabaseChangesOnStartup();

                opts.Policies.AutoApplyTransactions();

                opts.Services.AddResourceSetupOnStartup();
            })
            .StartAsync();

        _receivers.Add(host);

        return host;
    }

    private async Task cleanOldData(string connectionString)
    {
        await using var conn = new NpgsqlConnection(connectionString);
        await conn.OpenAsync();
        await conn.DropSchemaAsync("colors");
        await conn.DropSchemaAsync("color_sender");
        await conn.DropSchemaAsync("mt_queues");
        await conn.CloseAsync();
    }

    private async Task publishNumbers(string tenantId, List<ColorSum> colors)
    {
        await using var session = theSenderStore.LightweightSession(tenantId);

        while (colors.Any(x => !x.IsComplete()))
        {
            foreach (var color in colors)
            {
                if (color.IsComplete()) continue;

                color.PublishSome(session);
            }
        }

        await session.SaveChangesAsync();
    }

    [Fact]
    public async Task big_bang_end_to_end()
    {
        var receiver1 = await startNewReceiver();
        var receiver2 = await startNewReceiver();
        var receiver3 = await startNewReceiver();
        var receiver4 = await startNewReceiver();
        var receiver5 = await startNewReceiver();

        await receiver1.WaitUntilAssignmentsChangeTo(w =>
        {
            w.AgentScheme = StickyPostgresqlQueueListenerAgentFamily.StickyListenerSchema;

            w.ExpectRunningAgents(receiver1, 1);
            w.ExpectRunningAgents(receiver2, 1);
            w.ExpectRunningAgents(receiver3, 1);
            w.ExpectRunningAgents(receiver4, 1);
            w.ExpectRunningAgents(receiver5, 1);
        }, 60.Seconds());

        var data1 = new ColorData("tenant1", ["purple", "orange", "green", "red"]);
        var data2 = new ColorData("tenant2", ["purple", "orange", "green", "blue"]);
        var data3 = new ColorData("tenant3", ["purple", "blue", "green", "yellow"]);
        var data4 = new ColorData("tenant4", ["pink", "orange", "green", "red"]);

        ColorData[] all = [data1, data2, data3, data4];


        await Task.WhenAll(all.Select(x => x.PublishNumbers(theSenderStore)).ToArray());

        var receiverStore = receiver1.Services.GetRequiredService<IDocumentStore>();

        Func<Task<bool>> tryMatch = async () =>
        {
            foreach (var data in all)
            {
                await data.TryToMatch(receiverStore);
            }

            return all.All(x => x.HasMatched);
        };

        for (int i = 0; i < 20; i++)
        {
            var matched = await tryMatch();
            if (matched)
            {
                return;
            }

            await Task.Delay(250.Milliseconds());
        }

        throw new TimeoutException("The expected final state was never reached");
    }
}

public class ColorData
{
    public ColorData(string tenantId, string[] colors)
    {
        TenantId = tenantId;
        Data.AddRange(colors.Select(x => ColorSum.BuildRandom(x)));
    }

    public string TenantId { get; set; }
    public List<ColorSum> Data { get; } = new();

    public Task PublishNumbers(IDocumentStore store)
    {
        return Task.Factory.StartNew(async () =>
        {
            await using var session = store.LightweightSession(TenantId);

            while (Data.Any(x => !x.IsComplete()))
            {
                foreach (var color in Data)
                {
                    if (color.IsComplete()) continue;

                    color.PublishSome(session);
                }
            }

            await session.SaveChangesAsync();
        });


    }

    public bool HasMatched => Data.All(x => x.HasBeenMatched);

    public async Task TryToMatch(IDocumentStore store)
    {
        if (HasMatched) return;

        foreach (var sum in Data)
        {
            if (sum.HasBeenMatched) continue;

            await using var session = store.LightweightSession(TenantId);

            var loaded = await session.LoadAsync<ColorSum>(sum.Color);
            sum.HasBeenMatched = sum.Equals(loaded);
        }
    }
}

public record ColorsUpdated(string Color, int Number);

public class ColorSum
{
    public static ColorSum BuildRandom(string color)
    {
        var sum = new ColorSum { Color = color };
        for (int i = 0; i < Random.Shared.Next(50, 100); i++)
        {
            sum.Numbers.Add(Random.Shared.Next(0, 100));
        }

        return sum;
    }

    [Identity] public string Color { get; set; }

    public List<int> Numbers { get; set; } = new();

    public bool HasBeenMatched { get; set; }

    protected bool Equals(ColorSum other)
    {
        return Color == other.Color && Numbers.SequenceEqual(other.Numbers);
    }

    public override bool Equals(object? obj)
    {
        if (ReferenceEquals(null, obj))
        {
            return false;
        }

        if (ReferenceEquals(this, obj))
        {
            return true;
        }

        if (obj.GetType() != this.GetType())
        {
            return false;
        }

        return Equals((ColorSum)obj);
    }

    public override int GetHashCode()
    {
        return HashCode.Combine(Color, Numbers);
    }

    private Queue<int> _numbers;

    public void PublishSome(IDocumentSession session)
    {
        _numbers ??= new Queue<int>(Numbers);

        var numberOfEvents = Random.Shared.Next(1, _numbers.Count);
        for (int i = 0; i < numberOfEvents; i++)
        {
            session.Events.StartStream(Guid.NewGuid(), new ColorsUpdated(Color, _numbers.Dequeue()));
        }
    }

    public bool IsComplete()
    {
        return _numbers != null && !_numbers.Any();
    }
}

public record UpdateColorCounts(Guid Id);

public static class UpdateColorCountsHandler
{
    public static async Task Handle(UpdateColorCounts counts, IDocumentSession session)
    {
        var updates = await session.LoadAsync<ColorUpdates>(counts.Id);
        foreach (var pair in updates.Updates)
        {
            var doc = await session.LoadAsync<ColorSum>(pair.Key);
            doc ??= new ColorSum { Color = pair.Key };

            doc.Numbers.AddRange(pair.Value);

            session.Store(doc);
        }
    }
}

public record ColumnNumber(string Color, int Number);

public class ColorUpdates
{
    public Guid Id { get; set; }
    public Dictionary<string, List<int>> Updates { get; set; } = new();

    public void AddNumber(string color, int number)
    {
        if (!Updates.ContainsKey(color))
        {
            Updates[color] = new List<int>();
        }

        Updates[color].Add(number);
    }
}

public class ColorsSubscription : BatchSubscription
{
    public ColorsSubscription() : base("Colors")
    {
    }

    public override async Task ProcessEventsAsync(EventRange page, ISubscriptionController controller, IDocumentOperations operations,
        IMessageBus bus, CancellationToken cancellationToken)
    {
        var events = page.Events.Select(x => x.Data).OfType<ColorsUpdated>().ToArray();
        var updates = new ColorUpdates();

        foreach (var e in events)
        {
            updates.AddNumber(e.Color, e.Number);
        }

        operations.Store(updates);

        // Claim Check message
        await bus.PublishAsync(new UpdateColorCounts(updates.Id));
    }
}