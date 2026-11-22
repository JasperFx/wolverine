using System.Collections.Concurrent;
using IntegrationTests;
using JasperFx.Core;
using JasperFx.MultiTenancy;
using JasperFx.Resources;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Npgsql;
using Shouldly;
using Weasel.Postgresql;
using Weasel.Postgresql.Migrations;
using Wolverine;
using Wolverine.Persistence.Durability;
using Wolverine.Postgresql;
using Wolverine.Postgresql.Transport;
using Wolverine.RDBMS.MultiTenancy;
using Wolverine.Runtime;
using Wolverine.Tracking;
using Wolverine.Transports;

namespace PostgresqlTests.MultiTenancy;

// GH-5840. With master-table tenancy, removing a tenant used to leave every running node polling the dropped
// database (Npgsql errors on every poll) until the process restarted. An operator tool now calls
// WolverineRuntime.DeregisterTenantAsync on each node, which stops the tenant database's durability agent and
// queue listeners and forgets its message store.
public class deregistering_a_removed_tenant_5840 : PostgresqlContext, IAsyncLifetime
{
    private readonly ITestOutputHelper _output;
    private readonly string theSuffix = Guid.NewGuid().ToString("N")[..8];
    private readonly List<IHost> _hosts = [];
    private readonly ErrorLog theErrors = new();

    private string MainSchema => $"mt5840_{theSuffix}";
    private string DatabaseA => $"w5840_a_{theSuffix}";
    private string DatabaseB => $"w5840_b_{theSuffix}";

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
    }

    private static async Task dropDatabaseAsync(string database)
    {
        await using var conn = new NpgsqlConnection(Servers.PostgresConnectionString);
        await conn.OpenAsync();
        await using var command = conn.CreateCommand();
        command.CommandText = $"drop database if exists \"{database}\" with (force)";
        await command.ExecuteNonQueryAsync();
    }

    private async Task<IHost> startHostAsync(DurabilityMode mode, bool shareDatabaseA = false)
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

                opts.PersistMessagesWithPostgresql(Servers.PostgresConnectionString, MainSchema)
                    .UseMasterTableTenancy(seed =>
                    {
                        seed.Register("tenanta", connectionStringFor(DatabaseA));
                        seed.Register("tenantb", connectionStringFor(DatabaseB));

                        // A second tenant in tenant A's database, for the shared-database guard
                        if (shareDatabaseA) seed.Register("tenantc", connectionStringFor(DatabaseA));
                    })
                    .EnableMessageTransport(transport => transport.TransportSchemaName(MainSchema + "_queues"));

                opts.ListenToPostgresqlQueue("q5840");

                opts.Services.AddResourceSetupOnStartup();
                opts.Services.AddSingleton<ILoggerProvider>(theErrors);
                opts.Discovery.DisableConventionalDiscovery();
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
            $"{agentUri} to run");
    }

    private async Task assertTheDroppedDatabaseIsNotPolledAsync()
    {
        // The real failure: the database goes away and a node still polls it. Several poll cycles of the
        // durability agent and the queue listener later, nothing has failed.
        theErrors.Clear();
        await dropDatabaseAsync(DatabaseA);
        await Task.Delay(3.Seconds(), TestContext.Current.CancellationToken);

        foreach (var error in theErrors.Errors) _output.WriteLine(error);
        theErrors.Errors.ShouldBeEmpty();
    }

    [Fact]
    public async Task solo_node_stops_the_agent_and_listener_and_forgets_the_store()
    {
        var host = await startHostAsync(DurabilityMode.Solo);
        var runtime = host.GetRuntime();

        var storeA = await storeForAsync(host, "tenanta");
        var storeB = await storeForAsync(host, "tenantb");
        await waitUntilRunningAsync(host, storeA.Uri);
        await waitUntilRunningAsync(host, storeB.Uri);
        await waitForAsync(() => queueListener(host).IsListeningToDatabase(storeA.Name), "the tenant A listener");

        // The operator's sequence: the tenancy entry goes, every node deregisters the tenant
        var tenants = host.Services.GetServices<IDynamicTenantSource<string>>().OfType<MasterTenantSource>().Single();
        await tenants.RemoveTenantAsync("tenanta");

        var result = await runtime.DeregisterTenantAsync("tenanta", TestContext.Current.CancellationToken);

        result.StoppedAgents.ShouldBe([storeA.Uri]);
        result.DeregisteredStores.ShouldBe([storeA.Uri]);
        result.StoppedListeners.Count.ShouldBe(1);
        result.SharedStoresKept.ShouldBeEmpty();
        result.IsLeader.ShouldBeFalse();

        runtime.AllRunningAgentUris().ShouldNotContain(storeA.Uri);
        queueListener(host).IsListeningToDatabase(storeA.Name).ShouldBeFalse();
        (await runtime.Stores.FindAllAsync()).ShouldNotContain(x => x.Uri == storeA.Uri);
        await Should.ThrowAsync<RemovedTenantException>(async () =>
            await ((MultiTenantedMessageStore)runtime.Storage).GetDatabaseAsync("tenanta"));

        // Tenant B is untouched
        runtime.AllRunningAgentUris().ShouldContain(storeB.Uri);
        queueListener(host).IsListeningToDatabase(storeB.Name).ShouldBeTrue();

        // Idempotent
        var again = await runtime.DeregisterTenantAsync("tenanta", TestContext.Current.CancellationToken);
        again.StoppedAgents.ShouldBeEmpty();
        again.DeregisteredStores.ShouldBeEmpty();

        await assertTheDroppedDatabaseIsNotPolledAsync();

        // Nothing restarts it either: the solo loop restarts every agent it knows about
        await Task.Delay(2.Seconds(), TestContext.Current.CancellationToken);
        runtime.AllRunningAgentUris().ShouldNotContain(storeA.Uri);
    }

    [Fact]
    public async Task removal_while_the_agent_is_mid_poll_is_clean()
    {
        var host = await startHostAsync(DurabilityMode.Solo);
        var runtime = host.GetRuntime();

        var storeA = await storeForAsync(host, "tenanta");
        await waitUntilRunningAsync(host, storeA.Uri);

        // Polling every 100ms, and two deregistrations racing each other and the agent's own pass
        await Task.WhenAll(runtime.DeregisterTenantAsync("tenanta", TestContext.Current.CancellationToken), runtime.DeregisterTenantAsync("tenanta", TestContext.Current.CancellationToken));

        runtime.AllRunningAgentUris().ShouldNotContain(storeA.Uri);

        foreach (var error in theErrors.Errors) _output.WriteLine(error);
        theErrors.Errors.ShouldBeEmpty();

        await assertTheDroppedDatabaseIsNotPolledAsync();
    }

    [Fact]
    public async Task balanced_leader_drops_the_agent_and_never_reassigns_it()
    {
        var first = await startHostAsync(DurabilityMode.Balanced);
        var second = await startHostAsync(DurabilityMode.Balanced);

        var storeA = await storeForAsync(first, "tenanta");
        var hosts = new[] { first, second };
        await waitForAsync(() => hosts.Any(h => h.GetRuntime().AllRunningAgentUris().Contains(storeA.Uri)),
            "tenant A's agent to be assigned", 60.Seconds());
        await waitForAsync(() => hosts.Any(h => h.GetRuntime().IsLeader()), "a leader", 60.Seconds());

        var tenants = first.Services.GetServices<IDynamicTenantSource<string>>().OfType<MasterTenantSource>().Single();
        await tenants.RemoveTenantAsync("tenanta");

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

        // And the leader telling itself again is harmless
        var leader = hosts.Single(h => h.GetRuntime().IsLeader());
        (await leader.GetRuntime().DeregisterTenantAsync("tenanta", TestContext.Current.CancellationToken)).LeaderNotified.ShouldBeTrue();
    }

    [Fact]
    public async Task a_database_another_tenant_still_uses_is_left_running()
    {
        var host = await startHostAsync(DurabilityMode.Solo, shareDatabaseA: true);
        var runtime = host.GetRuntime();

        var storeA = await storeForAsync(host, "tenanta");
        await waitUntilRunningAsync(host, storeA.Uri);

        var result = await runtime.DeregisterTenantAsync("tenanta", TestContext.Current.CancellationToken);

        result.SharedStoresKept.ShouldBe([storeA.Uri]);
        result.DeregisteredStores.ShouldBeEmpty();
        result.StoppedAgents.ShouldBeEmpty();

        runtime.AllRunningAgentUris().ShouldContain(storeA.Uri);
        queueListener(host).IsListeningToDatabase(storeA.Name).ShouldBeTrue();
        var multiTenanted = (MultiTenantedMessageStore)runtime.Storage;
        (await multiTenanted.GetDatabaseAsync("tenantc")).Uri.ShouldBe(storeA.Uri);
        await Should.ThrowAsync<RemovedTenantException>(async () => await multiTenanted.GetDatabaseAsync("tenanta"));

        // The last tenant of the database going takes the database with it
        var last = await runtime.DeregisterTenantAsync("tenantc", TestContext.Current.CancellationToken);
        last.DeregisteredStores.ShouldBe([storeA.Uri]);
        last.StoppedAgents.ShouldBe([storeA.Uri]);
        runtime.AllRunningAgentUris().ShouldNotContain(storeA.Uri);
        queueListener(host).IsListeningToDatabase(storeA.Name).ShouldBeFalse();

        await assertTheDroppedDatabaseIsNotPolledAsync();
    }

    [Fact]
    public async Task adding_the_tenant_again_readmits_it()
    {
        var host = await startHostAsync(DurabilityMode.Solo);
        var runtime = host.GetRuntime();

        var storeA = await storeForAsync(host, "tenanta");
        await waitUntilRunningAsync(host, storeA.Uri);

        var tenants = host.Services.GetServices<IDynamicTenantSource<string>>().OfType<MasterTenantSource>().Single();
        await tenants.RemoveTenantAsync("tenanta");
        await runtime.DeregisterTenantAsync("tenanta", TestContext.Current.CancellationToken);
        runtime.AllRunningAgentUris().ShouldNotContain(storeA.Uri);

        await tenants.AddTenantAsync("tenanta", connectionStringFor(DatabaseA));

        var readmitted = await ((MultiTenantedMessageStore)runtime.Storage).GetDatabaseAsync("tenanta");
        readmitted.Uri.ShouldBe(storeA.Uri);

        // The solo loop picks the agent up again, and the queue listener the database
        await waitUntilRunningAsync(host, storeA.Uri);
        await waitForAsync(() => queueListener(host).IsListeningToDatabase(storeA.Name), "the tenant A listener again");
    }

    [Fact]
    public async Task pending_work_is_reported_per_tenant_database()
    {
        var host = await startHostAsync(DurabilityMode.Solo);
        var runtime = host.GetRuntime();

        var pending = await runtime.FindPendingWorkForTenantAsync("tenanta");

        var storeA = await storeForAsync(host, "tenanta");
        pending.Single().Database.ShouldBe(storeA.Uri);
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
