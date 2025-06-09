using JasperFx.Core;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using JasperFx.Resources;
using Wolverine.Runtime.Agents;
using Wolverine.Tracking;
using Xunit;
using Xunit.Abstractions;

namespace Wolverine.ComplianceTests;

public abstract class LeadershipElectionCompliance : IAsyncLifetime
{
    private readonly List<IHost> _hosts = new();
    private readonly ITestOutputHelper _output;
    private IHost _originalHost;
    
    // TODO -- rename this after combining
    protected abstract Task beforeBuildingHost();

    public LeadershipElectionCompliance(ITestOutputHelper output)
    {
        _output = output;
    }
    
    public async Task InitializeAsync()
    {
        await beforeBuildingHost();

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

    protected abstract void configureNode(WolverineOptions options);

    private async Task<IHost> startHostAsync()
    {
        var host = await Host.CreateDefaultBuilder()
            .UseWolverine(opts =>
            {
                opts.Durability.CheckAssignmentPeriod = 1.Seconds();
                opts.Durability.HealthCheckPollingTime = 1.Seconds();

                opts.Services.AddSingleton<IAgentFamily, FakeAgentFamily>();

                configureNode(opts);
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

    [Fact]
    public async Task the_only_known_node_is_automatically_the_leader()
    {
        await _originalHost.WaitUntilAssumesLeadershipAsync(10.Seconds());
    }

    /***** NEW TESTS START HERE **********************************************/

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
    public async Task leader_switchover_between_nodes()
    {
        await _originalHost.WaitUntilAssumesLeadershipAsync(5.Seconds());

        var host2 = await startHostAsync();
        var host3 = await startHostAsync();
        var host4 = await startHostAsync();

        await shutdownHostAsync(_originalHost);

        await host2.WaitUntilAssumesLeadershipAsync(30.Seconds());

        await shutdownHostAsync(host2);

        await host3.WaitUntilAssumesLeadershipAsync(30.Seconds());
    }

    [Fact]
    public async Task spin_up_several_nodes_take_away_original_node()
    {
        await _originalHost.WaitUntilAssumesLeadershipAsync(5.Seconds());

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
        await _originalHost.WaitUntilAssumesLeadershipAsync(5.Seconds());

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
    public async Task eject_a_stale_node()
    {
        await _originalHost.WaitUntilAssumesLeadershipAsync(10.Seconds());

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
        }, 45.Seconds());
    }

    [Fact]
    public async Task take_over_leader_ship_if_leader_becomes_stale()
    {
        await _originalHost.WaitUntilAssumesLeadershipAsync(5.Seconds());

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
        
        await host2.InvokeMessageAndWaitAsync(new CheckAgentHealth());
        await host2.WaitUntilAssumesLeadershipAsync(15.Seconds());


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