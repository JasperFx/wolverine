using System.Collections.Concurrent;
using IntegrationTests;
using JasperFx.Core;
using JasperFx.Resources;
using Marten;
using Marten.Storage;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Npgsql;
using Shouldly;
using Weasel.Postgresql;
using Weasel.Postgresql.Migrations;
using Wolverine;
using Wolverine.Marten;
using Wolverine.Persistence.Durability;
using Wolverine.Postgresql;
using Wolverine.Postgresql.Transport;
using Wolverine.Tracking;
using Wolverine.Transports;

namespace MartenTests.MultiTenancy;

// GH-5840. Marten master-table tenancy: deregistering a tenant on a running node stops its database's durability
// agent and queue listener and forgets its Wolverine message store, so the tenancy entry, the database and its
// role can then be removed without every node polling the dropped database until it restarts.
public class deregistering_a_removed_tenant_5840 : PostgresqlContext, IAsyncLifetime
{
    private readonly ITestOutputHelper _output;
    private readonly string theSuffix = Guid.NewGuid().ToString("N")[..8];
    private readonly List<IHost> _hosts = [];
    private readonly ErrorLog theErrors = new();

    private string TenancySchema => $"tenants5840_{theSuffix}";
    private string MessageSchema => $"mt5840_{theSuffix}";
    private string DatabaseA => $"m5840_a_{theSuffix}";
    private string DatabaseB => $"m5840_b_{theSuffix}";

    public deregistering_a_removed_tenant_5840(ITestOutputHelper output)
    {
        _output = output;
    }

    private static string connectionStringFor(string database)
    {
        return new NpgsqlConnectionStringBuilder(Servers.PostgresConnectionString) { Database = database }
            .ConnectionString;
    }

    public async ValueTask InitializeAsync()
    {
        await using var conn = new NpgsqlConnection(Servers.PostgresConnectionString);
        await conn.OpenAsync();

        foreach (var database in new[] { DatabaseA, DatabaseB })
        {
            if (!await conn.DatabaseExists(database))
            {
                await new DatabaseSpecification().BuildDatabase(conn, database);
            }
        }
    }

    public async ValueTask DisposeAsync()
    {
        foreach (var host in _hosts)
        {
            host.GetRuntime().Agents.DisableHealthChecks();
        }

        foreach (var host in _hosts)
        {
            await host.StopAsync();
            host.Dispose();
        }

        await dropDatabaseAsync(DatabaseA);
        await dropDatabaseAsync(DatabaseB);

        await using var conn = new NpgsqlConnection(Servers.PostgresConnectionString);
        await conn.OpenAsync();
        await conn.DropSchemaAsync(TenancySchema);
        await conn.DropSchemaAsync(MessageSchema);
    }

    private static async Task dropDatabaseAsync(string database)
    {
        await using var conn = new NpgsqlConnection(Servers.PostgresConnectionString);
        await conn.OpenAsync();
        await using var command = conn.CreateCommand();
        command.CommandText = $"drop database if exists \"{database}\" with (force)";
        await command.ExecuteNonQueryAsync();
    }

    private async Task seedTenantsAsync(bool shareDatabaseA)
    {
        var tenancy = new MasterTableTenancy(new StoreOptions { DatabaseSchemaName = TenancySchema },
            Servers.PostgresConnectionString, TenancySchema);
        await tenancy.ClearAllDatabaseRecordsAsync();
        await tenancy.AddDatabaseRecordAsync("tenanta", connectionStringFor(DatabaseA));
        await tenancy.AddDatabaseRecordAsync("tenantb", connectionStringFor(DatabaseB));

        // A second tenant with tenant A's connection string, for the shared-database guard
        if (shareDatabaseA) await tenancy.AddDatabaseRecordAsync("tenantc", connectionStringFor(DatabaseA));
    }

    private async Task<IHost> startHostAsync(DurabilityMode mode)
    {
        var host = await Host.CreateDefaultBuilder()
            .UseWolverine(opts =>
            {
                opts.Durability.Mode = mode;
                opts.Durability.HealthCheckPollingTime = 1.Seconds();
                opts.Durability.CheckAssignmentPeriod = 1.Seconds();
                opts.Durability.TenantCheckPeriod = 500.Milliseconds();

                // Polls the tenant databases constantly, so the removal lands while an agent is mid-poll
                opts.Durability.ScheduledJobPollingTime = 100.Milliseconds();
                opts.Durability.ScheduledJobFirstExecution = 0.Milliseconds();

                opts.Discovery.DisableConventionalDiscovery();
                opts.ListenToPostgresqlQueue("q5840");

                opts.Services.AddMarten(o =>
                    {
                        o.DisableNpgsqlLogging = true;
                        o.MultiTenantedDatabasesWithMasterDatabaseTable(Servers.PostgresConnectionString, TenancySchema);
                    })
                    .IntegrateWithWolverine(m =>
                    {
                        m.MessageStorageSchemaName = MessageSchema;
                        m.MainDatabaseConnectionString = Servers.PostgresConnectionString;
                    })
                    .ApplyAllDatabaseChangesOnStartup();

                opts.Services.AddResourceSetupOnStartup();
                opts.Services.AddSingleton<ILoggerProvider>(theErrors);
            }).StartAsync();

        _hosts.Add(host);
        return host;
    }

    private static async Task<IMessageStore> storeForAsync(IHost host, string tenantId)
    {
        return (await host.GetRuntime().Stores.FindForTenantAsync(tenantId)).Single();
    }

    private static MultiTenantedQueueListener queueListener(IHost host)
    {
        return host.GetRuntime().Endpoints.ActiveListeners().OfType<ListeningAgent>()
            .Select(x => x.Listener).OfType<MultiTenantedQueueListener>().Single();
    }

    private static async Task waitForAsync(Func<bool> condition, string description, TimeSpan? timeout = null)
    {
        var deadline = DateTimeOffset.UtcNow.Add(timeout ?? 30.Seconds());
        while (!condition())
        {
            if (DateTimeOffset.UtcNow > deadline) throw new TimeoutException("Timed out waiting for " + description);
            await Task.Delay(100);
        }
    }

    private static Task waitUntilRunningAsync(IHost host, Uri agentUri)
    {
        return waitForAsync(() => host.GetRuntime().AllRunningAgentUris().Contains(agentUri),
            $"{agentUri} to run", 60.Seconds());
    }

    private async Task removeTheTenancyEntryAndDatabaseAsync(IHost host)
    {
        // The rest of the operator's sequence, once every node has deregistered the tenant. Marten disposes the
        // tenant database's data source here, which the Wolverine store was built on.
        var tenancy = (MasterTableTenancy)host.Services.GetRequiredService<IDocumentStore>().Options.Tenancy;
        await tenancy.RemoveTenantAsync("tenanta");

        theErrors.Clear();
        await dropDatabaseAsync(DatabaseA);
        await Task.Delay(3.Seconds(), TestContext.Current.CancellationToken);

        foreach (var error in theErrors.Errors) _output.WriteLine(error);
        theErrors.Errors.ShouldBeEmpty();
    }

    [Fact]
    public async Task solo_node_stops_the_agent_and_listener_and_forgets_the_store()
    {
        await seedTenantsAsync(false);
        var host = await startHostAsync(DurabilityMode.Solo);
        var runtime = host.GetRuntime();

        var storeA = await storeForAsync(host, "tenanta");
        var storeB = await storeForAsync(host, "tenantb");
        await waitUntilRunningAsync(host, storeA.Uri);
        await waitUntilRunningAsync(host, storeB.Uri);
        await waitForAsync(() => queueListener(host).IsListeningToDatabase(storeA.Name), "the tenant A listener");

        var result = await runtime.DeregisterTenantAsync("tenanta", TestContext.Current.CancellationToken);

        result.StoppedAgents.ShouldBe([storeA.Uri]);
        result.DeregisteredStores.ShouldBe([storeA.Uri]);
        result.StoppedListeners.Count.ShouldBe(1);
        result.SharedStoresKept.ShouldBeEmpty();

        runtime.AllRunningAgentUris().ShouldNotContain(storeA.Uri);
        queueListener(host).IsListeningToDatabase(storeA.Name).ShouldBeFalse();
        (await runtime.Stores.FindAllAsync()).ShouldNotContain(x => x.Uri == storeA.Uri);
        await Should.ThrowAsync<RemovedTenantException>(async () =>
            await ((MultiTenantedMessageStore)runtime.Storage).GetDatabaseAsync("tenanta"));

        runtime.AllRunningAgentUris().ShouldContain(storeB.Uri);
        queueListener(host).IsListeningToDatabase(storeB.Name).ShouldBeTrue();

        // Idempotent
        (await runtime.DeregisterTenantAsync("tenanta", TestContext.Current.CancellationToken)).DeregisteredStores
            .ShouldBeEmpty();

        await removeTheTenancyEntryAndDatabaseAsync(host);
        runtime.AllRunningAgentUris().ShouldNotContain(storeA.Uri);
    }

    [Fact]
    public async Task balanced_leader_drops_the_agent_and_never_reassigns_it()
    {
        await seedTenantsAsync(false);
        var first = await startHostAsync(DurabilityMode.Balanced);
        var second = await startHostAsync(DurabilityMode.Balanced);
        var hosts = new[] { first, second };

        var storeA = await storeForAsync(first, "tenanta");
        await waitForAsync(() => hosts.Any(h => h.GetRuntime().AllRunningAgentUris().Contains(storeA.Uri)),
            "tenant A's agent to be assigned", 60.Seconds());
        await waitForAsync(() => hosts.Any(h => h.GetRuntime().IsLeader()), "a leader", 60.Seconds());

        // Only the node that is NOT the leader is told. The leader must hear it from that node.
        var follower = hosts.Single(h => !h.GetRuntime().IsLeader());
        var result = await follower.GetRuntime().DeregisterTenantAsync("tenanta", TestContext.Current.CancellationToken);
        result.LeaderNotified.ShouldBeTrue();

        await waitForAsync(() => hosts.All(h => !h.GetRuntime().AllRunningAgentUris().Contains(storeA.Uri)),
            "tenant A's agent to stop everywhere");

        // Several assignment evaluations later, it has not been put back anywhere
        await Task.Delay(4.Seconds(), TestContext.Current.CancellationToken);
        foreach (var host in hosts)
        {
            host.GetRuntime().AllRunningAgentUris().ShouldNotContain(storeA.Uri);
        }

        var leader = hosts.Single(h => h.GetRuntime().IsLeader());
        (await leader.GetRuntime().DeregisterTenantAsync("tenanta", TestContext.Current.CancellationToken))
            .LeaderNotified.ShouldBeTrue();
    }

    [Fact]
    public async Task a_database_another_tenant_still_uses_is_left_running()
    {
        await seedTenantsAsync(true);
        var host = await startHostAsync(DurabilityMode.Solo);
        var runtime = host.GetRuntime();

        var storeA = await storeForAsync(host, "tenanta");
        var storeC = await storeForAsync(host, "tenantc");
        storeC.Uri.ShouldBe(storeA.Uri);
        await waitUntilRunningAsync(host, storeA.Uri);
        await waitForAsync(() => queueListener(host).IsListeningToDatabase(storeC.Name), "the tenant C listener");

        var first = await runtime.DeregisterTenantAsync("tenanta", TestContext.Current.CancellationToken);

        first.SharedStoresKept.ShouldBe([storeA.Uri]);
        first.DeregisteredStores.ShouldBeEmpty();
        first.StoppedAgents.ShouldBeEmpty();

        await Task.Delay(1.Seconds(), TestContext.Current.CancellationToken);
        runtime.AllRunningAgentUris().ShouldContain(storeA.Uri);

        // Marten gives each of the two tenants its own store, and so its own listener, over the one database.
        // Tenant A's goes, before Marten disposes the data source under it; tenant C's carries on.
        queueListener(host).IsListeningToDatabase(storeA.Name).ShouldBeFalse();
        queueListener(host).IsListeningToDatabase(storeC.Name).ShouldBeTrue();

        var multiTenanted = (MultiTenantedMessageStore)runtime.Storage;
        (await multiTenanted.GetDatabaseAsync("tenantc")).Uri.ShouldBe(storeA.Uri);
        await Should.ThrowAsync<RemovedTenantException>(async () => await multiTenanted.GetDatabaseAsync("tenanta"));

        // The last tenant of the database going takes the database with it
        var last = await runtime.DeregisterTenantAsync("tenantc", TestContext.Current.CancellationToken);
        last.DeregisteredStores.ShouldBe([storeA.Uri]);
        last.StoppedAgents.ShouldBe([storeA.Uri]);

        await Task.Delay(2.Seconds(), TestContext.Current.CancellationToken);
        runtime.AllRunningAgentUris().ShouldNotContain(storeA.Uri);
        queueListener(host).IsListeningToDatabase(storeC.Name).ShouldBeFalse();

        foreach (var error in theErrors.Errors) _output.WriteLine(error);
        theErrors.Errors.ShouldBeEmpty();
    }

    internal class ErrorLog : ILoggerProvider
    {
        public ConcurrentQueue<string> Errors { get; private set; } = new();

        public void Clear() => Errors = new ConcurrentQueue<string>();

        public ILogger CreateLogger(string categoryName) => new CategoryLogger(this, categoryName);

        public void Dispose()
        {
        }

        private class CategoryLogger(ErrorLog parent, string category) : ILogger
        {
            public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
            public bool IsEnabled(LogLevel logLevel) => logLevel >= LogLevel.Error;

            public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception,
                Func<TState, Exception?, string> formatter)
            {
                if (logLevel < LogLevel.Error) return;
                parent.Errors.Enqueue($"{category}: {formatter(state, exception)} {exception?.GetType().Name}: {exception?.Message}");
            }
        }
    }
}
