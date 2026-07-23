using IntegrationTests;
using JasperFx.Core;
using JasperFx.Resources;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Npgsql;
using Shouldly;
using Weasel.Postgresql;
using Wolverine.ComplianceTests;
using Wolverine.Postgresql;
using Wolverine.Runtime;
using Wolverine.Runtime.Agents;
using Wolverine.Tracking;
using Wolverine.Util;
using Xunit;
using Xunit.Abstractions;

namespace Wolverine.RabbitMQ.Tests;

/// <summary>
/// GH-3604. When the node hosting an <see cref="ExclusiveListenerFamily"/> agent leaves the cluster, that
/// listener agent has to fail over to a surviving node and *settle there* -- exactly the way the per-database
/// durability agent and competing-consumer listeners do. This test kills the hosting node and asserts the
/// listener lands, running, on exactly one survivor and stays there.
/// </summary>
[Collection("multi-node-exclusive-failover")]
public class multi_node_exclusive_listener_failover : IAsyncLifetime
{
    private const string SchemaName = "multinode_listener_failover";
    private const string QueueName = "multinode-exclusive-failover";

    private readonly List<IHost> _hosts = [];
    private readonly ITestOutputHelper _output;

    public multi_node_exclusive_listener_failover(ITestOutputHelper output)
    {
        _output = output;
    }

    public async Task InitializeAsync()
    {
        await using var conn = new NpgsqlConnection(Servers.PostgresConnectionString);
        await conn.OpenAsync();
        await conn.DropSchemaAsync(SchemaName);
        await conn.CloseAsync();
    }

    public async Task DisposeAsync()
    {
        _hosts.Reverse();
        foreach (var host in _hosts.ToArray())
        {
            try
            {
                await shutdownHostAsync(host);
            }
            catch (Exception)
            {
                // Nothing useful to do about a host that won't shut down cleanly during teardown
            }
        }

        _hosts.Clear();
    }

    private async Task<IHost> startHostAsync()
    {
        var host = await Host.CreateDefaultBuilder().UseWolverine(opts =>
        {
            opts.ServiceName = "MultiNodeExclusiveFailover";

            opts.Durability.Mode = DurabilityMode.Balanced;
            opts.Durability.HealthCheckPollingTime = 1.Seconds();
            opts.Durability.NodeReassignmentPollingTime = 1.Seconds();
            opts.Durability.CheckAssignmentPeriod = 1.Seconds();

            // Aggressively short so a killed node is treated as gone quickly.
            opts.Durability.StaleNodeTimeout = 3.Seconds();

            opts.UseRabbitMq().EnableWolverineControlQueues().AutoProvision();
            opts.PersistMessagesWithPostgresql(Servers.PostgresConnectionString, SchemaName);

            opts.Discovery.DisableConventionalDiscovery();

            opts.ListenToRabbitQueue(QueueName)
                .ListenWithStrictOrdering()
                .UseDurableInbox();

            opts.Services.AddSingleton<ILoggerProvider>(new OutputLoggerProvider(_output));
            opts.Services.AddResourceSetupOnStartup();
        }).StartAsync();

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

    private static int nodeNumberOf(IHost host) => host.GetRuntime().DurabilitySettings.AssignedNodeNumber;

    private IHost? listenerHost()
    {
        return _hosts.FirstOrDefault(h =>
            h.RunningAgents().Any(uri => uri.Scheme == ExclusiveListenerFamily.SchemeName &&
                                         uri.AbsolutePath.EndsWith(QueueName, StringComparison.OrdinalIgnoreCase)));
    }

    private async Task<IHost> waitForListenerAsync(TimeSpan timeout)
    {
        var stopwatch = System.Diagnostics.Stopwatch.StartNew();
        while (stopwatch.Elapsed < timeout)
        {
            var host = listenerHost();
            if (host != null) return host;
            await Task.Delay(200.Milliseconds());
        }

        listenerHost().ShouldNotBeNull($"No node ever started the exclusive listener agent for {QueueName}");
        throw new TimeoutException();
    }

    /// <summary>
    /// The listener must not only appear on a survivor, it must *stay* there -- the GH-3604 symptom was a
    /// listener that flapped, briefly starting on a survivor and then dropping off every node.
    /// </summary>
    private async Task<IHost> waitForStableListenerAsync(TimeSpan timeout)
    {
        var stopwatch = System.Diagnostics.Stopwatch.StartNew();
        while (stopwatch.Elapsed < timeout)
        {
            var first = listenerHost();
            if (first != null)
            {
                // Has to hold the same node across a couple of health-check cycles to count as settled.
                await Task.Delay(2.Seconds());
                var second = listenerHost();
                if (second != null && ReferenceEquals(first, second))
                {
                    return second;
                }
            }

            await Task.Delay(500.Milliseconds());
        }

        throw new TimeoutException(
            "The exclusive listener agent never settled on a single surviving node -- it kept flapping.");
    }

    private int listenerHostCount() => _hosts.Count(h => h.RunningAgents().Any(uri =>
        uri.Scheme == ExclusiveListenerFamily.SchemeName &&
        uri.AbsolutePath.EndsWith(QueueName, StringComparison.OrdinalIgnoreCase)));

    private static Uri listenerAgentUri() =>
        new Uri($"{ExclusiveListenerFamily.SchemeName}://rabbitmq/{QueueName}");

    private static async Task pinListenerToAsync(IHost leader, int nodeNumber)
    {
        var restrictions = new AgentRestrictions();
        restrictions.PinAgent(listenerAgentUri(), nodeNumber);
        await leader.GetRuntime().Agents.ApplyRestrictionsAsync(restrictions, CancellationToken.None);
    }

    private async Task<IHost> waitForListenerOnAsync(int nodeNumber, TimeSpan timeout)
    {
        var stopwatch = System.Diagnostics.Stopwatch.StartNew();
        while (stopwatch.Elapsed < timeout)
        {
            var host = listenerHost();
            if (host != null && nodeNumberOf(host) == nodeNumber) return host;
            await Task.Delay(200.Milliseconds());
        }

        throw new TimeoutException($"The exclusive listener never moved to node {nodeNumber}");
    }

    [Fact]
    public async Task listener_fails_over_to_a_survivor_when_its_node_is_stopped_gracefully()
    {
        var leader = await startHostAsync();
        await startHostAsync();
        await startHostAsync();

        (await leader.WaitUntilAssumesLeadershipAsync(30.Seconds()))
            .ShouldBeTrue("The first host never assumed leadership");

        var originalListener = await waitForListenerAsync(30.Seconds());
        var originalNode = nodeNumberOf(originalListener);
        _output.WriteLine($"Exclusive listener initially on node {originalNode}");

        // Cleanly stop the node hosting the listener.
        await shutdownHostAsync(originalListener);
        _output.WriteLine($"Stopped node {originalNode}; waiting for failover");

        var survivor = await waitForStableListenerAsync(90.Seconds());
        var survivorNode = nodeNumberOf(survivor);

        _output.WriteLine($"Exclusive listener settled on node {survivorNode}");

        survivorNode.ShouldNotBe(originalNode);
        listenerHostCount().ShouldBe(1);
    }

    /// <summary>
    /// The failure mode GH-3604 chased: a node that *crashes* rather than shutting down cleanly. It stops
    /// participating in cluster coordination and its persisted heartbeat goes stale, but it never gets a
    /// chance to hand its agents off. The leader has to notice the stale node and reassign the exclusive
    /// listener to a survivor -- and it must *settle*, not flap.
    /// </summary>
    [Fact]
    public async Task listener_fails_over_to_a_survivor_when_its_node_crashes()
    {
        var leader = await startHostAsync();
        await startHostAsync();
        await startHostAsync();

        (await leader.WaitUntilAssumesLeadershipAsync(30.Seconds()))
            .ShouldBeTrue("The first host never assumed leadership");

        var originalListener = await waitForListenerAsync(30.Seconds());
        var originalNode = nodeNumberOf(originalListener);
        _output.WriteLine($"Exclusive listener initially on node {originalNode}");

        // Simulate an ungraceful crash: stop the health-check loop and backdate the persisted heartbeat so
        // the leader treats this node as stale, WITHOUT cleanly detaching its agents -- exactly what a hard
        // process kill leaves behind. After this the node reports no running agents (NodeController is torn
        // down), so it drops out of the survivor set on its own.
        await originalListener.GetRuntime().DisableAgentsAsync(DateTimeOffset.UtcNow.AddHours(-1));
        _output.WriteLine($"Crashed node {originalNode}; waiting for failover");

        var survivor = await waitForStableListenerAsync(90.Seconds());
        var survivorNode = nodeNumberOf(survivor);

        _output.WriteLine($"Exclusive listener settled on node {survivorNode}");

        survivorNode.ShouldNotBe(originalNode);
        listenerHostCount().ShouldBe(1);
    }

    /// <summary>
    /// The most turbulent failover: the node that crashes is the *leader* AND it is running the exclusive
    /// listener. A fresh leader has to be elected and, on the same beat, reassign the orphaned listener to a
    /// survivor. This is where a reassignment that never settles would show up worst.
    /// </summary>
    [Fact]
    public async Task listener_fails_over_when_the_leader_running_it_crashes()
    {
        var leader = await startHostAsync();
        await startHostAsync();
        await startHostAsync();

        (await leader.WaitUntilAssumesLeadershipAsync(30.Seconds()))
            .ShouldBeTrue("The first host never assumed leadership");

        // Force the listener onto the leader so the crash takes out both roles at once.
        await waitForListenerAsync(30.Seconds());
        await pinListenerToAsync(leader, nodeNumberOf(leader));
        await waitForListenerOnAsync(nodeNumberOf(leader), 30.Seconds());

        var leaderNode = nodeNumberOf(leader);
        _output.WriteLine($"Leader (node {leaderNode}) is running the exclusive listener; crashing it");

        await leader.GetRuntime().DisableAgentsAsync(DateTimeOffset.UtcNow.AddHours(-1));

        var survivor = await waitForStableListenerAsync(90.Seconds());
        var survivorNode = nodeNumberOf(survivor);

        _output.WriteLine($"Exclusive listener settled on node {survivorNode} after the leader crashed");

        survivorNode.ShouldNotBe(leaderNode);
        listenerHostCount().ShouldBe(1);
    }
}
