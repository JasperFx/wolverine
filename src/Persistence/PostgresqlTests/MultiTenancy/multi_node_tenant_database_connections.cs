using IntegrationTests;
using JasperFx.Core;
using JasperFx.Resources;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Npgsql;
using Shouldly;
using Weasel.Postgresql;
using Weasel.Postgresql.Migrations;
using Wolverine;
using Wolverine.Persistence;
using Wolverine.Postgresql;
using Wolverine.Runtime.Agents;
using Wolverine.Tracking;
using Xunit.Abstractions;

namespace PostgresqlTests.MultiTenancy;

// GH-3376: with multi-tenancy, every node was polling every tenant database for scheduled messages
// every ScheduledJobPollingTime, completely outside the agent distribution machinery. The per-database
// advisory lock deduped the WORK across nodes but not the CONNECTION - the losing node still opened a
// pooled connection, began a transaction, failed the lock, and rolled back. At 512 tenants x N nodes
// that parks ~512 connections per node, which is what was reported on the issue.
//
// This test attributes connections to nodes by stamping a distinct ApplicationName into each host's
// connection strings, which is the only way pg_stat_activity can tell two Wolverine nodes apart.
public class multi_node_tenant_database_connections : PostgresqlContext, IAsyncLifetime
{
    private readonly ITestOutputHelper _output;
    private readonly List<IHost> _hosts = [];

    // Shared across both hosts so "exactly once" counts executions on either node.
    private readonly TenantMessageTracker theTracker = new();

    // Unique per run: leftover databases/schemas from a prior run otherwise break resource setup.
    private readonly string theSuffix = Guid.NewGuid().ToString("N")[..8];
    private readonly string[] theTenants = ["red", "blue", "green"];

    private string MainSchema => $"mt3376_{theSuffix}";
    private string TenantDatabase(string tenant) => $"w3376_{tenant}_{theSuffix}";

    public multi_node_tenant_database_connections(ITestOutputHelper output)
    {
        _output = output;
    }

    private string ConnectionStringFor(string database, string nodeName)
    {
        return new NpgsqlConnectionStringBuilder(Servers.PostgresConnectionString)
        {
            Database = database,

            // The whole point: pg_stat_activity has no idea which Wolverine node owns a backend,
            // so tag every connection this host opens with the node's name.
            ApplicationName = nodeName
        }.ConnectionString;
    }

    public async Task InitializeAsync()
    {
        await using var conn = new NpgsqlConnection(Servers.PostgresConnectionString);
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

    private async Task<IHost> StartHostAsync(string nodeName)
    {
        var host = await Host.CreateDefaultBuilder()
            .UseWolverine(opts =>
            {
                opts.Durability.Mode = DurabilityMode.Balanced;

                // Tighten the loops so distribution settles and polling is observable
                // well inside the test's patience.
                opts.Durability.HealthCheckPollingTime = 1.Seconds();
                opts.Durability.CheckAssignmentPeriod = 1.Seconds();
                opts.Durability.ScheduledJobPollingTime = 1.Seconds();
                opts.Durability.ScheduledJobFirstExecution = 500.Milliseconds();

                opts.PersistMessagesWithPostgresql(
                        ConnectionStringFor("postgres", nodeName), MainSchema)
                    .RegisterStaticTenants(tenants =>
                    {
                        foreach (var tenant in theTenants)
                        {
                            tenants.Register(tenant, ConnectionStringFor(TenantDatabase(tenant), nodeName));
                        }
                    });

                opts.Services.AddResourceSetupOnStartup();
                opts.Services.AddSingleton(theTracker);

                opts.Policies.UseDurableLocalQueues();

                opts.Discovery.DisableConventionalDiscovery()
                    .IncludeType<TenantScheduledMessageHandler>();
            }).StartAsync();

        _hosts.Add(host);
        return host;
    }

    public async Task DisposeAsync()
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
    }

    [Fact]
    public async Task tenant_databases_are_only_polled_by_their_owning_node()
    {
        var leader = await StartHostAsync("node-A");
        await leader.WaitUntilAssumesLeadershipAsync(30.Seconds());

        var second = await StartHostAsync("node-B");

        // main + one per tenant == 4 durability agents, distributed evenly across two nodes
        var settled = await leader.WaitUntilAssignmentsChangeTo(w =>
        {
            w.AgentScheme = PersistenceConstants.AgentScheme;
            w.ExpectRunningAgents(leader, 2);
            w.ExpectRunningAgents(second, 2);
        }, 60.Seconds());

        settled.ShouldBeTrue("Durability agents never distributed evenly across the two nodes");

        foreach (var host in _hosts)
        {
            foreach (var uri in host.RunningAgents().Where(x => x.Scheme == PersistenceConstants.AgentScheme))
            {
                _output.WriteLine($"{host.GetRuntime().Options.Durability.AssignedNodeNumber}: {uri}");
            }
        }

        // Open a measurement window and let the scheduled-job pollers (1s cadence) turn over
        // several times inside it.
        var windowStart = await ServerNowAsync();
        await Task.Delay(6.Seconds());

        var offenders = new List<string>();

        foreach (var tenant in theTenants)
        {
            var databaseName = TenantDatabase(tenant);
            var nodes = await FindActiveNodesSinceAsync(databaseName, windowStart);

            _output.WriteLine($"{databaseName}: queried by [{nodes.Join(", ")}]");

            if (nodes.Count > 1)
            {
                offenders.Add($"{databaseName} was queried by {nodes.Count} nodes: {nodes.Join(", ")}");
            }
        }

        offenders.ShouldBeEmpty();
    }

    [Fact]
    public async Task scheduled_tenant_message_still_executes_exactly_once()
    {
        var leader = await StartHostAsync("node-A");
        await leader.WaitUntilAssumesLeadershipAsync(30.Seconds());
        var second = await StartHostAsync("node-B");

        await leader.WaitUntilAssignmentsChangeTo(w =>
        {
            w.AgentScheme = PersistenceConstants.AgentScheme;
            w.ExpectRunningAgents(leader, 2);
            w.ExpectRunningAgents(second, 2);
        }, 60.Seconds());

        var messageId = Guid.NewGuid();
        await leader.MessageBus().ScheduleAsync(new TenantScheduledMessage(messageId), 1.Seconds(),
            new DeliveryOptions { TenantId = "red" });

        var handled = await Poll(30.Seconds(), () => theTracker.Received.Any(r => r.Id == messageId));
        handled.ShouldBeTrue($"Scheduled message {messageId} for tenant 'red' was never executed");

        // Give the losing node's poller every chance to run it a second time before counting.
        await Task.Delay(5.Seconds());

        theTracker.Received.Count(r => r.Id == messageId).ShouldBe(1);
    }

    private static async Task<bool> Poll(TimeSpan timeout, Func<bool> condition)
    {
        using var cts = new CancellationTokenSource(timeout);
        while (!cts.IsCancellationRequested)
        {
            if (condition()) return true;
            await Task.Delay(250.Milliseconds());
        }

        return condition();
    }

    private async Task<DateTime> ServerNowAsync()
    {
        await using var conn = new NpgsqlConnection(Servers.PostgresConnectionString);
        await conn.OpenAsync();
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = "select clock_timestamp()";
        return (DateTime)(await cmd.ExecuteScalarAsync())!;
    }

    // Which of our nodes actually issued a query against this database inside the window?
    //
    // Mere connection presence is not evidence of polling: every node migrates every tenant database
    // at startup, and Npgsql parks those connections in the pool long afterward. query_start advancing
    // past the window's start is what separates an ongoing poller from a parked leftover. Timestamps
    // all come from the server's own clock, so the test host's clock never enters into it.
    private async Task<List<string>> FindActiveNodesSinceAsync(string databaseName, DateTime windowStart)
    {
        await using var conn = new NpgsqlConnection(Servers.PostgresConnectionString);
        await conn.OpenAsync();

        await using var cmd = conn.CreateCommand();
        cmd.CommandText = @"
select distinct application_name
from pg_stat_activity
where datname = @db
  and application_name like 'node-%'
  and query_start > @windowStart
order by application_name";
        cmd.Parameters.AddWithValue("@db", databaseName);
        cmd.Parameters.AddWithValue("@windowStart", windowStart);

        var names = new List<string>();
        await using var reader = await cmd.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            names.Add(await reader.GetFieldValueAsync<string>(0));
        }

        return names;
    }
}
