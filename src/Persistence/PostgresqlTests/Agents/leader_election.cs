using IntegrationTests;
using JasperFx.Core;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Npgsql;
using Oakton.Resources;
using Shouldly;
using TestingSupport;
using Weasel.Postgresql;
using Wolverine;
using Wolverine.Logging;
using Wolverine.Postgresql;
using Wolverine.Runtime.Agents;
using Wolverine.Tracking;
using Xunit.Abstractions;

namespace PostgresqlTests.Agents;

public class leader_election : PostgresqlContext, IAsyncLifetime
{
    private readonly List<IHost> _hosts = new();
    private readonly ITestOutputHelper _output;
    private IHost _originalHost;

    public leader_election(ITestOutputHelper output)
    {
        _output = output;
    }

    public async Task InitializeAsync()
    {
        await dropSchema();

        _originalHost = await startHostAsync();
    }

    public async Task DisposeAsync()
    {
        foreach (var host in _hosts)
        {
            host.GetRuntime().Agents.DisableHealthChecks();
        }

        _hosts.Reverse();
        foreach (var host in _hosts.ToArray())
        {
            await shutdownHostAsync(host);
        }
    }

    private async Task<IHost> startHostAsync()
    {
        var host = await Host.CreateDefaultBuilder()
            .UseWolverine(opts =>
            {
                opts.Durability.CheckAssignmentPeriod = 1.Seconds();
                opts.Durability.HealthCheckPollingTime = 1.Seconds();

                opts.Services.AddSingleton<IAgentFamily, FakeAgentFamily>();

                opts.PersistMessagesWithPostgresql(Servers.PostgresConnectionString, "registry");
                opts.Services.AddSingleton<ILoggerProvider>(new OutputLoggerProvider(_output));

                opts.Services.AddResourceSetupOnStartup();
            }).StartAsync();

        new XUnitEventObserver(host, _output);

        _hosts.Add(host);

        return host;
    }

    private async Task shutdownHostAsync(IHost host)
    {
        host.GetRuntime().Agents.DisableHealthChecks();
        await host.StopAsync();
        host.Dispose();
        _hosts.Remove(host);
    }

    private static async Task dropSchema()
    {
        using var conn = new NpgsqlConnection(Servers.PostgresConnectionString);
        await conn.OpenAsync();
        await conn.DropSchemaAsync("registry");
        await conn.CloseAsync();
    }

    [Fact]
    public async Task the_only_known_node_is_automatically_the_leader()
    {
        var tracker = _originalHost.GetRuntime().Tracker;
        await tracker.WaitUntilAssumesLeadershipAsync(10.Seconds());
        tracker.IsLeader().ShouldBeTrue();
    }

    /***** NEW TESTS START HERE **********************************************/

    private bool allAgentsAreRunning(WolverineTracker tracker)
    {
        var agents = new FakeAgentFamily().AllAgentUris();
        return agents.All(tracker.AgentIsRunning);
    }

    [Fact]
    public async Task the_original_node_knows_about_all_the_possible_agents()
    {
        await _originalHost.WaitUntilAssignmentsChangeTo(w => w.ExpectRunningAgents(_originalHost, 12), 20.Seconds());
    }

    [Fact]
    public async Task add_second_node_see_balanced_nodes()
    {
        var host2 = await startHostAsync();

        await _originalHost.WaitUntilAssignmentsChangeTo(w =>
        {
            w.ExpectRunningAgents(_originalHost, 6);
            w.ExpectRunningAgents(host2, 6);
        }, 60.Seconds());
    }

    /***** NEW TESTS END HERE **********************************************/

    [Fact]
    public async Task send_node_event_for_starting_on_startup()
    {
        var tracker = _originalHost.GetRuntime().Tracker;
        await tracker.WaitUntilAssumesLeadershipAsync(5.Seconds());

        var waiter = _originalHost.GetRuntime().Tracker.WaitForNodeEvent(NodeEventType.Started, 10.Seconds());

        var host2 = await startHostAsync();

        var @event = await waiter;

        var runtime2 = host2.GetRuntime();
        @event.Node.Id.ShouldBe(runtime2.Options.UniqueNodeId);

        _originalHost.GetRuntime().Tracker.Nodes.ContainsKey(runtime2.Options.UniqueNodeId).ShouldBeTrue();

        // Should not take over leadership
        runtime2.Tracker.IsLeader().ShouldBeFalse();
        _originalHost.GetRuntime().Tracker.IsLeader().ShouldBeTrue();
    }

    [Fact]
    public async Task send_node_event_for_exiting_node()
    {
        var host2 = await startHostAsync();

        // This is just to eliminate some errors in test output
        await _originalHost.WaitUntilAssignmentsChangeTo(w =>
        {
            w.ExpectRunningAgents(_originalHost, 6);
            w.ExpectRunningAgents(host2, 6);
        }, 30.Seconds());

        var waiter = _originalHost.GetRuntime().Tracker.WaitForNodeEvent(NodeEventType.Exiting, 10.Seconds());
        var host2Id = host2.GetRuntime().Options.UniqueNodeId;

        await shutdownHostAsync(host2);

        var @event = await waiter;

        @event.Node.Id.ShouldBe(host2Id);

        _originalHost.GetRuntime().Tracker.Nodes.Count.ShouldBe(1);

        _originalHost.GetRuntime().Tracker.Nodes.ContainsKey(host2Id).ShouldBeFalse();
    }

    [Fact]
    public async Task leader_switchover_between_nodes()
    {
        var tracker = _originalHost.GetRuntime().Tracker;
        await tracker.WaitUntilAssumesLeadershipAsync(5.Seconds());

        var host2 = await startHostAsync();
        var host3 = await startHostAsync();
        var host4 = await startHostAsync();

        await shutdownHostAsync(_originalHost);

        await host2.GetRuntime().Tracker.WaitUntilAssumesLeadershipAsync(30.Seconds());

        await shutdownHostAsync(host2);

        await host3.GetRuntime().Tracker.WaitUntilAssumesLeadershipAsync(30.Seconds());
    }

    [Fact]
    public async Task spin_up_several_nodes_take_away_original_node()
    {
        var tracker = _originalHost.GetRuntime().Tracker;
        await tracker.WaitUntilAssumesLeadershipAsync(5.Seconds());

        var host2 = await startHostAsync();
        var host3 = await startHostAsync();
        var host4 = await startHostAsync();

        // This is just to eliminate some errors in test output
        await _originalHost.WaitUntilAssignmentsChangeTo(w =>
        {
            w.ExpectRunningAgents(_originalHost, 3);
            w.ExpectRunningAgents(host2, 3);
            w.ExpectRunningAgents(host3, 3);
            w.ExpectRunningAgents(host4, 3);
        }, 30.Seconds());

        await shutdownHostAsync(_originalHost);

        await host2.WaitUntilAssignmentsChangeTo(w =>
        {
            w.ExpectRunningAgents(host2, 4);
            w.ExpectRunningAgents(host3, 4);
            w.ExpectRunningAgents(host4, 4);
        }, 30.Seconds());
    }

    [Fact]
    public async Task spin_up_several_nodes_take_away_non_leader_node()
    {
        var tracker = _originalHost.GetRuntime().Tracker;
        await tracker.WaitUntilAssumesLeadershipAsync(5.Seconds());

        var host2 = await startHostAsync();
        var host3 = await startHostAsync();
        var host4 = await startHostAsync();

        await shutdownHostAsync(host3);

        await _originalHost.WaitUntilAssignmentsChangeTo(w =>
        {
            w.ExpectRunningAgents(host2, 4);
            w.ExpectRunningAgents(_originalHost, 4);
            w.ExpectRunningAgents(host4, 4);
        }, 30.Seconds());
    }

    [Fact]
    public async Task verify_assignments_can_make_corrections()
    {
        var tracker = _originalHost.GetRuntime().Tracker;
        await tracker.WaitUntilAssumesLeadershipAsync(20.Seconds());

        var host2 = await startHostAsync();
        var host3 = await startHostAsync();
        var host4 = await startHostAsync();

        // This is just to eliminate some errors in test output
        await _originalHost.WaitUntilAssignmentsChangeTo(w =>
        {
            w.ExpectRunningAgents(_originalHost, 3);
            w.ExpectRunningAgents(host2, 3);
            w.ExpectRunningAgents(host3, 3);
            w.ExpectRunningAgents(host4, 3);
        }, 30.Seconds());

        var runtime4 = host4.GetRuntime();
        foreach (var agentUri in runtime4.Agents.AllRunningAgentUris())
        {
            await runtime4.Agents.StopLocallyAsync(agentUri);
        }

        // This should eventually turn back on the missing agents from node4
        await _originalHost.InvokeMessageAndWaitAsync(new VerifyAssignments());

        await _originalHost.WaitUntilAssignmentsChangeTo(w =>
        {
            w.ExpectRunningAgents(_originalHost, 3);
            w.ExpectRunningAgents(host2, 3);
            w.ExpectRunningAgents(host3, 3);
            w.ExpectRunningAgents(host4, 3);
        }, 30.Seconds());
    }

    [Fact]
    public async Task eject_a_stale_node()
    {
        var tracker = _originalHost.GetRuntime().Tracker;
        await tracker.WaitUntilAssumesLeadershipAsync(5.Seconds());

        var host2 = await startHostAsync();
        var host3 = await startHostAsync();
        var host4 = await startHostAsync();

        // This is just to eliminate some errors in test output
        await _originalHost.WaitUntilAssignmentsChangeTo(w =>
        {
            w.ExpectRunningAgents(_originalHost, 3);
            w.ExpectRunningAgents(host2, 3);
            w.ExpectRunningAgents(host3, 3);
            w.ExpectRunningAgents(host4, 3);
        }, 30.Seconds());

        var runtime4 = host4.GetRuntime();

        await runtime4.DisableAgentsAsync(DateTimeOffset.UtcNow.AddHours(-1));

        await _originalHost.InvokeMessageAndWaitAsync(new CheckAgentHealth());

        await _originalHost.WaitUntilAssignmentsChangeTo(w =>
        {
            w.ExpectRunningAgents(_originalHost, 4);
            w.ExpectRunningAgents(host2, 4);
            w.ExpectRunningAgents(host3, 4);
        }, 30.Seconds());
    }

    [Fact]
    public async Task take_over_leader_ship_if_leader_becomes_stale()
    {
        var tracker = _originalHost.GetRuntime().Tracker;
        await tracker.WaitUntilAssumesLeadershipAsync(5.Seconds());

        var host2 = await startHostAsync();
        var host3 = await startHostAsync();
        var host4 = await startHostAsync();

        // This is just to eliminate some errors in test output
        await _originalHost.WaitUntilAssignmentsChangeTo(w =>
        {
            w.ExpectRunningAgents(_originalHost, 3);
            w.ExpectRunningAgents(host2, 3);
            w.ExpectRunningAgents(host3, 3);
            w.ExpectRunningAgents(host4, 3);
        }, 30.Seconds());

        await _originalHost.GetRuntime().DisableAgentsAsync(DateTimeOffset.UtcNow.AddHours(-1));

        var runtime2 = host2.GetRuntime();
        await host2.InvokeMessageAndWaitAsync(new CheckAgentHealth());
        await runtime2.Tracker.WaitUntilAssumesLeadershipAsync(15.Seconds());


        await host2.WaitUntilAssignmentsChangeTo(w =>
        {
            w.ExpectRunningAgents(host2, 4);
            w.ExpectRunningAgents(host3, 4);
            w.ExpectRunningAgents(host4, 4);
        }, 30.Seconds());
    }

    [Fact]
    public async Task persist_and_load_node_records()
    {
        var records = new NodeRecord[]
        {
            new NodeRecord
            {
                Description = "one",
                NodeNumber = 1,
                RecordType = NodeRecordType.NodeStarted
            },
            new NodeRecord
            {
                Description = "one",
                NodeNumber = 1,
                RecordType = NodeRecordType.NodeStopped
            },
            new NodeRecord
            {
                Description = "one",
                NodeNumber = 2,
                RecordType = NodeRecordType.NodeStarted
            },
            new NodeRecord
            {
                Description = "one",
                NodeNumber = 2,
                RecordType = NodeRecordType.NodeStopped
            },
        };

        var runtime = _originalHost.GetRuntime();

        var nodes = runtime.Storage.Nodes;
        await nodes.LogRecordsAsync(records);

        var count = 0;
        while (count < 10)
        {
            var persisted = await nodes.FetchRecentRecordsAsync(10);
            if (persisted.Count >= 4) return;
        }

        throw new Exception("No persisted node records!");
    }
}