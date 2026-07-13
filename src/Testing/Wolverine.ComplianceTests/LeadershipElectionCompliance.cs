using System.Collections.Concurrent;
using JasperFx.Core;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using JasperFx.Resources;
using Shouldly;
using Wolverine.Runtime;
using Wolverine.Runtime.Agents;
using Wolverine.Tracking;
using Xunit;
using Xunit.Abstractions;

namespace Wolverine.ComplianceTests;

public abstract class LeadershipElectionCompliance : IAsyncLifetime
{
    private readonly List<IHost> _hosts = new();
    private readonly ITestOutputHelper _output;
    private IHost _originalHost = null!;
    
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

        await AfterDisposeAsync();
    }

    /// <summary>
    /// Template method for transport-specific cleanup after hosts are disposed.
    /// </summary>
    protected virtual Task AfterDisposeAsync()
    {
        return Task.CompletedTask;
    }

    public IHost FindHostRunning(Uri agentUri)
    {
        if (_originalHost.RunningAgents().Contains(agentUri)) return _originalHost;

        return _hosts.FirstOrDefault(x => x.RunningAgents().Contains(agentUri))!;
    }

    public IHost OriginalHost => _originalHost;

    protected abstract void configureNode(WolverineOptions options);

    protected async Task<IHost> startHostAsync()
    {
        return await startHostAsync(null);
    }

    protected async Task<IHost> startHostAsync(Action<WolverineOptions>? configure)
    {
        var host = await Host.CreateDefaultBuilder()
            .UseWolverine(opts =>
            {
                opts.Discovery.IncludeAssembly(GetType().Assembly).IncludeType(typeof(DoOnAgentHandler)).IncludeType(typeof(TriggerFanOutHandler)).IncludeType(typeof(FanOutHandler));
                
                opts.Durability.CheckAssignmentPeriod = 1.Seconds();
                opts.Durability.HealthCheckPollingTime = 1.Seconds();

                opts.Services.AddSingleton<IAgentFamily, FakeAgentFamily>();

                configureNode(opts);
                configure?.Invoke(opts);
                opts.Services.AddSingleton<ILoggerProvider>(new OutputLoggerProvider(_output));

                opts.Services.AddResourceSetupOnStartup();

                #region sample_register_singular_agent
                // Little extension method helper on IServiceCollection to register your
                // SingularAgent
                opts.Services.AddSingularAgent<SimpleSingularAgent>();

                #endregion
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

    [Fact]
    public async Task pausing_an_agent_stops_it_immediately()
    {
        await _originalHost.WaitUntilAssignmentsChangeTo(w => w.ExpectRunningAgents(_originalHost, 12), 30.Seconds());

        var runtime = _originalHost.GetRuntime();

        // Health checks are disabled for the rest of this test on purpose. The health-check loop
        // re-reads restrictions from the database and would eventually stop the agent anyway, which
        // would mask the defect: GH-698 / CritterWatch#698 is that ApplyRestrictionsAsync persisted
        // the restriction and then DISCARDED the StopRemoteAgent that EvaluateAssignmentsAsync
        // returned, so the pause had no *immediate* effect. Isolating that path is the whole point.
        runtime.Agents.DisableHealthChecks();

        var uri = runtime.Agents.AllRunningAgentUris().First();

        var restrictions = new AgentRestrictions([]);
        restrictions.PauseAgent(uri);

        await runtime.Agents.ApplyRestrictionsAsync(restrictions, CancellationToken.None);

        // No polling: by the time ApplyRestrictionsAsync returns, the stop must have been carried out.
        runtime.Agents.AllRunningAgentUris().ShouldNotContain(uri);

        // ...and the in-memory copy that ListeningAgent consults must reflect it too, otherwise a
        // paused listener would just restart itself on the next attempt.
        runtime.Restrictions.FindPausedAgentUris().ShouldContain(uri);
    }

    /***** NEW TESTS END HERE **********************************************/

    [Fact]
    public async Task singular_agent_is_only_running_on_one()
    {
        var host2 = await startHostAsync();
        var host3 = await startHostAsync();
        var host4 = await startHostAsync();
        
        await _originalHost.WaitUntilAssumesLeadershipAsync(5.Seconds());

        await shutdownHostAsync(_originalHost);

        var uri = new Uri("simple://");

        using var cancellation = new CancellationTokenSource();
        cancellation.CancelAfter(15.Seconds());
        while (!cancellation.IsCancellationRequested && !_hosts.Any(x => x.RunningAgents().Contains(uri)))
        {
            await Task.Delay(250.Milliseconds());
        }
        
        _hosts.SelectMany(x => x.RunningAgents()).Where(x => x == uri)
            .Count().ShouldBe(1);
    }

    private static void ConfigureSlowHeartbeat(WolverineOptions o)
    {
        o.Durability.HealthCheckPollingTime = 10.Minutes();
        o.Durability.StaleNodeTimeout = 15.Minutes();
    }

    [Fact]
    public async Task leader_switchover_between_nodes()
    {
        await _originalHost.WaitUntilAssumesLeadershipAsync(5.Seconds());
        var host2 = await startHostAsync();
        var host3 = await startHostAsync(ConfigureSlowHeartbeat);
        var host4 = await startHostAsync(ConfigureSlowHeartbeat);

        await shutdownHostAsync(_originalHost);

        await host2.WaitUntilAssumesLeadershipAsync(30.Seconds());

        await shutdownHostAsync(host2);

        // host3 has a 10-minute heartbeat, so trigger a health check
        // explicitly to acquire the lock after host2 releases it.
        await host3.InvokeMessageAndWaitAsync(new CheckAgentHealth());
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

        // All remaining nodes use a 10-minute heartbeat interval so no
        // background loop races for the leadership lock after host1 is
        // disabled. The sole trigger is the CheckAgentHealth message
        // sent explicitly to host2 below.
        var host2 = await startHostAsync(ConfigureSlowHeartbeat);
        var host3 = await startHostAsync(ConfigureSlowHeartbeat);
        var host4 = await startHostAsync(ConfigureSlowHeartbeat);

        // This is just to eliminate some errors in test output
        await _originalHost.WaitUntilAssignmentsChangeTo(w =>
        {
            w.ExpectRunningAgents(_originalHost, 3);
            w.ExpectRunningAgents(host2, 3);
            w.ExpectRunningAgents(host3, 3);
            w.ExpectRunningAgents(host4, 3);
        }, 30.Seconds());

        await _originalHost.GetRuntime().DisableAgentsAsync(DateTimeOffset.UtcNow.AddHours(-1));

        // No background heartbeats can interfere - the triggered
        // CheckAgentHealth is the only path acquiring the lock.
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
    public async Task take_over_leader_ship_if_leader_becomes_stale_with_racing_nodes()
    {
        // Realistic scenario: the leader is disabled and the remaining
        // nodes compete for leadership through their natural 1-second
        // heartbeat loops. No explicit CheckAgentHealth messages.
        // Any node may win; we just verify one does and the cluster
        // rebalances correctly.
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

        // Wait for any of the remaining 3 nodes to assume leadership
        // via their natural heartbeat loop. With all nodes on a 1s
        // interval, at least one should grab the lock before timeout
        await WaitForAnyLeadershipAsync([host2, host3, host4], 15.Seconds());

        await host2.WaitUntilAssignmentsChangeTo(w =>
        {
            w.ExpectRunningAgents(host2, 4);
            w.ExpectRunningAgents(host3, 4);
            w.ExpectRunningAgents(host4, 4);
        }, 30.Seconds());
    }

    private static async Task WaitForAnyLeadershipAsync(IHost[] hosts, TimeSpan timeout)
    {
        using var cts = new CancellationTokenSource(timeout);
        while (!cts.IsCancellationRequested)
        {
            if (hosts.Any(h => h.GetRuntime().IsLeader())) return;
            await Task.Delay(25.Milliseconds(), cts.Token);
        }
        throw new TimeoutException($"No node assumed leadership within {timeout}");
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

    [Fact]
    public async Task fan_out_message_to_all_nodes()
    {
        FanOutHandler.Handled.Clear();

        await _originalHost.WaitUntilAssumesLeadershipAsync(5.Seconds());

        var host2 = await startHostAsync();
        var host3 = await startHostAsync();

        await _originalHost.WaitUntilAssignmentsChangeTo(w =>
        {
            w.ExpectRunningAgents(_originalHost, 4);
            w.ExpectRunningAgents(host2, 4);
            w.ExpectRunningAgents(host3, 4);
        }, 30.Seconds());

        // Send TriggerFanOut to node1; its handler calls FanOutToAllNodes(new FannedOut())
        await _originalHost.InvokeMessageAndWaitAsync(new TriggerFanOut());

        var nodes = new[] { _originalHost, host2, host3 };
        var expectedNodeNumbers = nodes.Select(x => x.NodeNumber()).ToHashSet();

        // Poll for delivery to all 3 nodes (control queue messages are not tracked)
        using var cancellation = new CancellationTokenSource();
        cancellation.CancelAfter(15.Seconds());
        while (!cancellation.IsCancellationRequested)
        {
            var handledNumbers = FanOutHandler.Handled.ToHashSet();
            if (expectedNodeNumbers.All(n => handledNumbers.Contains(n))) break;
            await Task.Delay(250.Milliseconds());
        }

        var finalHandled = FanOutHandler.Handled.ToHashSet();
        foreach (var expected in expectedNodeNumbers)
        {
            finalHandled.ShouldContain(expected);
        }
    }

    [Fact]
    public async Task ability_to_send_messages_to_correct_node_or_forward()
    {
        DoOnAgentHandler.Handled.Clear();
        
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

        var family = new FakeAgentFamily();
        var allAgents = family.AllAgentUris();

        foreach (var agentUri in allAgents)
        {
            await _originalHost.InvokeMessageAndWaitAsync(new DoOnAgent(agentUri));
        }

        var nodes = new[]{_originalHost, host2, host3, host4};
        
        // We purposely ignore messages to the control queues in the track activity, so boohoo,
        // gotta do this

        for (var i = 0; i < 10; i++)
        {
            if (DoOnAgentHandler.Handled.Count >= allAgents.Length) break;

            await Task.Delay(100.Milliseconds());
        }
        
        foreach (var agent in allAgents)
        {
            var handled = DoOnAgentHandler.Handled.First(x => x.AgentUri == agent);
            var node = nodes.First(x => x.NodeNumber() == handled.NodeNumber);
            node.RunningAgents().ShouldContain(agent);
        }
        
        
    }
}

public record DoOnAgent(Uri AgentUri);

public record AgentHandled(Uri AgentUri, int NodeNumber);

public static class DoOnAgentHandler
{
    public static ConcurrentBag<AgentHandled> Handled = new();

    public static async Task HandleAsync(DoOnAgent command, IMessageContext context, CancellationToken cancellationToken)
    {
        await context.InvokeOnAgentOrForwardAsync(command.AgentUri, (runtime, c) =>
        {
            runtime.Agents.AllRunningAgentUris().ShouldContain(command.AgentUri);
            Handled.Add(new AgentHandled(command.AgentUri, runtime.DurabilitySettings.AssignedNodeNumber));

            return Task.CompletedTask;
        }, cancellationToken);
    }
}

public record TriggerFanOut;

public record FannedOut;

public static class TriggerFanOutHandler
{
    public static async Task HandleAsync(TriggerFanOut command, IMessageContext context)
    {
        await context.FanOutToAllNodes(new FannedOut());
    }
}

public static class FanOutHandler
{
    public static ConcurrentBag<int> Handled = new();

    public static void Handle(FannedOut message, IWolverineRuntime runtime)
    {
        Handled.Add(runtime.DurabilitySettings.AssignedNodeNumber);
    }
}