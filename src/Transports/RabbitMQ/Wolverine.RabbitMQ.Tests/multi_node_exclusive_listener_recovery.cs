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
using Wolverine.Persistence;
using Wolverine.Persistence.Durability;
using Wolverine.Postgresql;
using Wolverine.Runtime;
using Wolverine.Runtime.Agents;
using Wolverine.Tracking;
using Wolverine.Transports;
using Wolverine.Util;
using Xunit;
using Xunit.Abstractions;

namespace Wolverine.RabbitMQ.Tests;

/// <summary>
/// GH-3597, the multi-node end to end coverage for the GH-3590 fix. The single-host compliance suite pins the
/// <i>semantics</i> — the listening node recovers its own inbox, the durability agent leaves those rows alone —
/// but it stands in for "the listener lives on another node" with a registered-but-not-started endpoint in a
/// Solo host. What the customer actually hit was real agent distribution putting the per-database
/// <see cref="PersistenceConstants.AgentScheme"/> durability agent and the
/// <see cref="ExclusiveListenerFamily"/> listener agent on genuinely different nodes.
///
/// So: real Balanced hosts, real leader election, real Rabbit MQ listeners, one shared PostgreSQL store.
/// </summary>
[Collection("multi-node-exclusive-recovery")]
public class multi_node_exclusive_listener_recovery : IAsyncLifetime
{
    private const string SchemaName = "multinode_listeners";
    private const string QueueName = "multinode-exclusive-recovery";

    private readonly List<IHost> _hosts = [];
    private readonly ITestOutputHelper _output;

    public multi_node_exclusive_listener_recovery(ITestOutputHelper output)
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
            opts.ServiceName = "MultiNodeExclusiveRecovery";

            // Balanced is the whole point -- this test exists because agents distribute.
            opts.Durability.Mode = DurabilityMode.Balanced;
            opts.Durability.HealthCheckPollingTime = 1.Seconds();
            opts.Durability.NodeReassignmentPollingTime = 1.Seconds();

            // Speed up how quickly a pinned reassignment (the agent-separation nudge) takes hold.
            opts.Durability.CheckAssignmentPeriod = 1.Seconds();

            // The recovery loop polls at ScheduledJobPollingTime; keep it tight so the test doesn't
            // wait out the 5 second default on every sweep.
            opts.Durability.ScheduledJobPollingTime = 250.Milliseconds();

            opts.UseRabbitMq().EnableWolverineControlQueues().AutoProvision();
            opts.PersistMessagesWithPostgresql(Servers.PostgresConnectionString, SchemaName);

            opts.Discovery.DisableConventionalDiscovery();
            opts.Discovery.IncludeType<NodeTrackedMessageHandler>();

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
        // Disabling health checks first matters for deterministic teardown -- otherwise the surviving
        // nodes can start a reassignment mid-shutdown.
        host.GetRuntime().Agents.DisableHealthChecks();
        await host.StopAsync();
        host.Dispose();
        _hosts.Remove(host);
    }

    private static Uri queueUri() => $"rabbitmq://queue/{QueueName}".ToUri();

    private static int nodeNumberOf(IHost host) => host.GetRuntime().DurabilitySettings.AssignedNodeNumber;

    /// <summary>
    /// The host currently running the <see cref="ExclusiveListenerFamily"/> agent for the queue under test.
    /// </summary>
    private IHost? listenerHost()
    {
        return _hosts.FirstOrDefault(h =>
            h.RunningAgents().Any(uri => uri.Scheme == ExclusiveListenerFamily.SchemeName &&
                                         uri.AbsolutePath.EndsWith(QueueName, StringComparison.OrdinalIgnoreCase)));
    }

    /// <summary>
    /// The host currently running the per-database durability agent.
    /// </summary>
    private IHost? durabilityAgentHost()
    {
        return _hosts.FirstOrDefault(h =>
            h.RunningAgents().Any(uri => uri.Scheme == PersistenceConstants.AgentScheme));
    }

    private static Uri listenerAgentUri() =>
        new Uri($"{ExclusiveListenerFamily.SchemeName}://rabbitmq/{QueueName}");

    /// <summary>
    /// Both agent families have to settle, and crucially they have to settle on <i>different</i> nodes — that
    /// separation is the entire premise of GH-3590. Wolverine's distribution is free to co-locate them, and with
    /// three nodes and two agents it frequently does, so rather than skipping (which would mean the test quietly
    /// proves nothing most of the time) this nudges the listener onto a different node with an agent pin.
    /// </summary>
    private async Task<(IHost Listener, IHost DurabilityAgent)> waitForSeparatedAgentsAsync(IHost leader,
        TimeSpan timeout)
    {
        var stopwatch = System.Diagnostics.Stopwatch.StartNew();
        var nudged = false;

        while (stopwatch.Elapsed < timeout)
        {
            var listener = listenerHost();
            var agent = durabilityAgentHost();

            if (listener != null && agent != null)
            {
                if (!ReferenceEquals(listener, agent))
                {
                    return (listener, agent);
                }

                if (!nudged)
                {
                    var target = _hosts.FirstOrDefault(h => !ReferenceEquals(h, agent));

                    target.ShouldNotBeNull("Need at least two live nodes to separate the two agent families");

                    _output.WriteLine(
                        $"Nudging the exclusive listener onto node {nodeNumberOf(target!)} " +
                        $"(durability agent is on node {nodeNumberOf(agent)})");

                    await pinListenerToAsync(leader, nodeNumberOf(target!));
                    nudged = true;
                }
            }

            await Task.Delay(250.Milliseconds());
        }

        listenerHost().ShouldNotBeNull($"No node ever started the exclusive listener agent for {QueueName}");
        durabilityAgentHost().ShouldNotBeNull("No node ever started the per-database durability agent");

        throw new TimeoutException(
            "The exclusive listener agent and the per-database durability agent never landed on different nodes, " +
            "even after pinning the listener. This test only proves anything when they are separated.");
    }

    private static async Task pinListenerToAsync(IHost leader, int nodeNumber)
    {
        var restrictions = new AgentRestrictions();
        restrictions.PinAgent(listenerAgentUri(), nodeNumber);
        await leader.GetRuntime().Agents.ApplyRestrictionsAsync(restrictions, CancellationToken.None);
    }


    private static async Task<Envelope[]> seedDormantMessagesAsync(IHost host, int count)
    {
        var runtime = host.GetRuntime();
        var store = host.Services.GetRequiredService<IMessageStore>();
        var serializer = runtime.Options.DefaultSerializer!;

        var envelopes = Enumerable.Range(0, count).Select(i =>
        {
            var id = Guid.NewGuid();
            var envelope = new Envelope(new NodeTrackedMessage(id, i))
            {
                Id = id,
                Destination = queueUri(),
                Status = EnvelopeStatus.Incoming,

                // Exactly how an ungraceful shutdown leaves them, and how the durability agent releases
                // the rows a dead node owned.
                OwnerId = TransportConstants.AnyNode,
                ContentType = serializer.ContentType,
                MessageType = typeof(NodeTrackedMessage).ToMessageTypeName(),
                SentAt = DateTimeOffset.UtcNow
            };

            envelope.Data = serializer.Write(envelope);

            return envelope;
        }).ToArray();

        await store.Inbox.StoreIncomingAsync(envelopes);

        return envelopes;
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

    [Fact]
    public async Task the_listening_node_recovers_the_inbox_when_the_durability_agent_is_elsewhere()
    {
        using var tracking = NodeTrackedMessages.Track();

        var leader = await startHostAsync();
        await startHostAsync();
        await startHostAsync();

        (await leader.WaitUntilAssumesLeadershipAsync(30.Seconds()))
            .ShouldBeTrue("The first host never assumed leadership");

        var (listener, agentHost) = await waitForSeparatedAgentsAsync(leader, 60.Seconds());

        var listenerNode = nodeNumberOf(listener);
        var agentNode = nodeNumberOf(agentHost);

        _output.WriteLine($"Exclusive listener on node {listenerNode}, durability agent on node {agentNode}");

        // This is the precondition the whole test rests on -- the deadlock GH-3590 fixed only exists when
        // these two agents are on different nodes.
        listenerNode.ShouldNotBe(agentNode);

        var seeded = await seedDormantMessagesAsync(listener, 5);
        var expected = seeded.Select(x => x.Id).ToArray();

        var recovered = await waitForAsync(() => expected.All(tracking.Contains), 60.Seconds());

        recovered.ShouldBeTrue(
            $"Expected all {seeded.Length} dormant inbox messages to be recovered, but only saw {tracking.Count}. " +
            $"Before GH-3590 these stranded forever whenever the durability agent (node {agentNode}) was not " +
            $"the node hosting the exclusive listener (node {listenerNode}).");

        // Recovered *by the listening node*, not by whichever node happens to hold the durability agent.
        foreach (var id in expected)
        {
            tracking.NodeFor(id).ShouldBe(listenerNode);
        }

        // ...which means the durability agent's node never claimed a single one.
        tracking.Nodes.ShouldNotContain(agentNode);
    }

    /// <summary>
    /// The ordering that motivated a <i>polling</i> recovery loop rather than a one-shot sweep when the
    /// listener reaches Accepting. In the real failure, rows owned by a node that died are released back to
    /// <c>owner_id = 0</c> by whichever node holds that database's durability agent, and that release
    /// routinely lands *after* the exclusive listener has already restarted somewhere else. A listener that
    /// swept once at startup would have already looked, seen nothing dormant, and never looked again.
    ///
    /// Reproduced here without killing a node: the rows start out owned by a node number that is not in the
    /// cluster (exactly what a dead node's rows look like), so the listener's startup sweep finds nothing,
    /// and only later are they released to <c>owner_id = 0</c>.
    /// </summary>
    [Fact]
    public async Task rows_released_after_the_listener_is_already_running_are_still_recovered()
    {
        using var tracking = NodeTrackedMessages.Track();

        var leader = await startHostAsync();
        await startHostAsync();
        await startHostAsync();

        (await leader.WaitUntilAssumesLeadershipAsync(30.Seconds()))
            .ShouldBeTrue("The first host never assumed leadership");

        var (listener, _) = await waitForSeparatedAgentsAsync(leader, 60.Seconds());
        var listenerNode = nodeNumberOf(listener);

        var store = listener.Services.GetRequiredService<IMessageStore>();
        var seeded = await seedDormantMessagesAsync(listener, 5);

        // Owned by a node that is not part of this cluster -- the state a dead node's in-flight batch is in
        // before the durability agent gets around to releasing it.
        const int departedNode = 987654;
        await store.ReassignIncomingAsync(departedNode, seeded);

        (await store.LoadPageOfGloballyOwnedIncomingAsync(queueUri(), 100))
            .ShouldBeEmpty("Nothing should be dormant yet -- the rows are still owned by the departed node");

        // Several recovery sweeps go by with nothing to find. This is the window a one-shot recovery would
        // have spent its single look in.
        await Task.Delay(2.Seconds());

        tracking.Count.ShouldBe(0,
            "The listener must not touch inbox rows that are still owned by another node");

        // Now the durability agent releases the departed node's rows, long after the listener started.
        await store.ReassignIncomingAsync(TransportConstants.AnyNode, seeded);

        var expected = seeded.Select(x => x.Id).ToArray();
        var recovered = await waitForAsync(() => expected.All(tracking.Contains), 60.Seconds());

        recovered.ShouldBeTrue(
            $"Expected the {seeded.Length} rows released after the listener was already running to be picked " +
            $"up by a later recovery poll, but only saw {tracking.Count}. A one-shot sweep at startup would " +
            $"never see these.");

        foreach (var id in expected)
        {
            tracking.NodeFor(id).ShouldBe(listenerNode);
        }
    }
}

public record NodeTrackedMessage(Guid Id, int Sequence);

public class NodeTrackedMessageHandler
{
    public static void Handle(NodeTrackedMessage message, DurabilitySettings settings)
    {
        NodeTrackedMessages.Record(message, settings.AssignedNodeNumber);
    }
}

/// <summary>
/// Static handler state is process wide, and all three hosts run in this one process, so what matters is
/// recording <i>which node</i> handled each message. That attribution is the assertion: it is what separates
/// "the listening node recovered its own inbox" from "somebody recovered it".
/// </summary>
public static class NodeTrackedMessages
{
    private static readonly object _locker = new();
    private static NodeTrackedMessageTracking? _current;

    public static NodeTrackedMessageTracking Track()
    {
        lock (_locker)
        {
            _current = new NodeTrackedMessageTracking();
            return _current;
        }
    }

    internal static void Record(NodeTrackedMessage message, int nodeNumber)
    {
        lock (_locker)
        {
            _current?.Record(message, nodeNumber);
        }
    }

    internal static void Clear(NodeTrackedMessageTracking tracking)
    {
        lock (_locker)
        {
            if (ReferenceEquals(_current, tracking))
            {
                _current = null;
            }
        }
    }
}

public class NodeTrackedMessageTracking : IDisposable
{
    private readonly object _locker = new();
    private readonly Dictionary<Guid, int> _received = new();

    internal void Record(NodeTrackedMessage message, int nodeNumber)
    {
        lock (_locker)
        {
            _received[message.Id] = nodeNumber;
        }
    }

    public int Count
    {
        get
        {
            lock (_locker)
            {
                return _received.Count;
            }
        }
    }

    public bool Contains(Guid id)
    {
        lock (_locker)
        {
            return _received.ContainsKey(id);
        }
    }

    public int NodeFor(Guid id)
    {
        lock (_locker)
        {
            return _received.TryGetValue(id, out var node) ? node : 0;
        }
    }

    public IReadOnlyList<int> Nodes
    {
        get
        {
            lock (_locker)
            {
                return _received.Values.Distinct().ToArray();
            }
        }
    }

    public void Dispose()
    {
        NodeTrackedMessages.Clear(this);
    }
}
