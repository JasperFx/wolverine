using JasperFx.Core;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Wolverine.Transports;

namespace Wolverine.Runtime.Agents;

public partial class NodeAgentController
{
    public static readonly Uri LeaderUri = new("wolverine://leader");

    private readonly Dictionary<string, IAgentFamily>
        _agentFamilies = new();

    private readonly CancellationTokenSource _cancellation;
    private readonly ILogger _logger;
    private readonly INodeAgentPersistence _persistence;

    private readonly IWolverineRuntime _runtime;
    private readonly INodeStateTracker _tracker;

    private ImHashMap<Uri, IAgent> _agents = ImHashMap<Uri, IAgent>.Empty;

    // May be valuable later
    private DateTimeOffset? _lastAssignmentCheck;


    internal NodeAgentController(IWolverineRuntime runtime, INodeStateTracker tracker,
        INodeAgentPersistence persistence,
        IEnumerable<IAgentFamily> agentControllers, ILogger logger, CancellationToken cancellation)
    {
        _runtime = runtime;
        _tracker = tracker;
        _persistence = persistence;
        foreach (var agentController in agentControllers)
        {
            _agentFamilies[agentController.Scheme] = agentController;
        }

        if (runtime.Options.Durability.Mode == DurabilityMode.Balanced)
        {
            _agentFamilies[ExclusiveListenerFamily.SchemeName] = new ExclusiveListenerFamily(runtime);
        }

        if (runtime.Options.Durability.DurabilityAgentEnabled)
        {
            var family = _runtime.Storage.BuildAgentFamily(runtime);
            if (family != null)
            {
                _agentFamilies[family.Scheme] = family;
            }
        }

        foreach (var family in runtime.Options.Transports.OfType<IAgentFamilySource>().SelectMany(x => x.BuildAgentFamilySources(runtime)))
        {
            _agentFamilies[family.Scheme] = family;
        }

        _cancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellation);
        _logger = logger;
    }

    public bool HasStartedInSoloMode { get; private set; }

    internal void AddHandlers(WolverineRuntime runtime)
    {
        var handlers = runtime.Handlers;

        handlers.RegisterMessageType(typeof(StartAgent));
        handlers.RegisterMessageType(typeof(StartAgents));
        handlers.RegisterMessageType(typeof(AgentsStarted));
        handlers.RegisterMessageType(typeof(AgentsStopped));
        handlers.RegisterMessageType(typeof(StopAgent));
        handlers.RegisterMessageType(typeof(StopAgents));
        handlers.RegisterMessageType(typeof(QueryAgents));
        handlers.RegisterMessageType(typeof(NodeEvent));
        handlers.RegisterMessageType(typeof(TryAssumeLeadership));
    }

    public async Task StopAsync(IMessageBus messageBus)
    {
        await stopAllAgentsAsync();

        if (_runtime.Options.Durability.Mode == DurabilityMode.Balanced)
        {
            await informOtherNodesAboutExitingAsync(messageBus);
        }

        try
        {
            await _persistence.DeleteAsync(_runtime.Options.UniqueNodeId);
            await _persistence.LogRecordsAsync(NodeRecord.For(_runtime.Options, NodeRecordType.NodeStopped));
        }
        catch (Exception e)
        {
            _logger.LogError(e, "Error trying to delete the exiting node from node persistence");
        }
    }

    private async Task informOtherNodesAboutExitingAsync(IMessageBus messageBus)
    {
        try
        {
            if (_tracker.Self!.IsLeader())
            {
                // notify everyone
                // Don't trust the in memory storage of nodes, fetch from storage
                var controlUris = await _persistence.LoadAllOtherNodeControlUrisAsync(_tracker.Self.Id);
                foreach (var uri in controlUris)
                    await messageBus.EndpointFor(uri).SendAsync(new NodeEvent(_tracker.Self, NodeEventType.Exiting));
            }
            else
            {
                // Don't trust the in memory storage of nodes, fetch from storage
                // ONLY notify the leader. Makes tests work better:)
                var controlUri = await _persistence.FindLeaderControlUriAsync(_tracker.Self.Id);

                if (controlUri != null)
                {
                    await messageBus.EndpointFor(controlUri)
                        .SendAsync(new NodeEvent(_tracker.Self, NodeEventType.Exiting));
                }
            }
        }
        catch (Exception e)
        {
            _logger.LogError(e, "Error trying to notify other nodes about this node exiting");
        }
    }

    private async Task stopAllAgentsAsync()
    {
        foreach (var entry in _agents.Enumerate())
        {
            try
            {
                await entry.Value.StopAsync(CancellationToken.None);
            }
            catch (Exception e)
            {
                _logger.LogError(e, "Error trying to stop agent {AgentUri}", entry.Value.Uri);
            }
        }
    }

    private ValueTask<IAgent> findAgentAsync(Uri uri)
    {
        if (_agentFamilies.TryGetValue(uri.Scheme, out var controller))
        {
            return controller.BuildAgentAsync(uri, _runtime);
        }

        throw new ArgumentOutOfRangeException(nameof(uri), $"Unrecognized agent scheme '{uri.Scheme}'");
    }

    public async Task StartAgentAsync(Uri agentUri)
    {
        if (_agents.Contains(agentUri))
        {
            return;
        }

        var agent = await findAgentAsync(agentUri);
        try
        {
            await agent.StartAsync(_cancellation.Token);
            await _persistence.LogRecordsAsync(NodeRecord.For(_runtime.Options, NodeRecordType.AgentStarted,
                agentUri));

            // Need to update the current node
            _tracker.Publish(new AgentStarted(_runtime.Options.UniqueNodeId, agentUri));

            _logger.LogInformation("Successfully started agent {AgentUri} on Node {NodeNumber}", agentUri,
                _runtime.Options.Durability.AssignedNodeNumber);
        }
        catch (Exception e)
        {
            throw new AgentStartingException(agentUri, _runtime.Options.UniqueNodeId, e);
        }

        _agents = _agents.AddOrUpdate(agentUri, agent);

        try
        {
            await _persistence.AddAssignmentAsync(_runtime.Options.UniqueNodeId, agentUri, _cancellation.Token);
        }
        catch (Exception e)
        {
            _logger.LogError(e, "Error trying to persist the assignment of agent {AgentUri} to Node {NodeId}", agentUri,
                _runtime.Options.UniqueNodeId);
        }
    }

    public async Task StopAgentAsync(Uri agentUri)
    {
        if (_agents.TryFind(agentUri, out var agent))
        {
            try
            {
                await agent.StopAsync(_cancellation.Token);
                _logger.LogInformation("Successfully stopped agent {AgentUri} on node {NodeNumber}", agentUri,
                    _runtime.Options.Durability.AssignedNodeNumber);
                await _persistence.LogRecordsAsync(NodeRecord.For(_runtime.Options, NodeRecordType.AgentStopped,
                    agentUri));
            }
            catch (Exception e)
            {
                throw new AgentStoppingException(agentUri, _runtime.Options.UniqueNodeId, e);
            }

            _agents = _agents.Remove(agentUri);
        }

        try
        {
            await _persistence.RemoveAssignmentAsync(_runtime.Options.UniqueNodeId, agentUri, _cancellation.Token);
        }
        catch (Exception e)
        {
            _logger.LogError(e,
                "Error trying to remove the assignment of agent {AgentUri} to Node {NodeId} in persistence", agentUri,
                _runtime.Options.UniqueNodeId);
        }
    }

    public Uri[] AllRunningAgentUris()
    {
        return _agents.Enumerate().Select(x => x.Key).ToArray();
    }

    /// <summary>
    ///     THIS IS STRICTLY FOR TESTING
    /// </summary>
    internal async Task DisableAgentsAsync()
    {
        var agents = _agents.Enumerate().Select(x => x.Value).ToArray();
        foreach (var agent in agents) await agent.StopAsync(CancellationToken.None);

        _agents = ImHashMap<Uri, IAgent>.Empty;
    }
}

public class AgentStartingException : Exception
{
    public AgentStartingException(Uri agentUri, Guid nodeId, Exception? innerException) : base(
        $"Failed trying to start agent {agentUri} on node {nodeId}", innerException)
    {
    }
}

public class AgentStoppingException : Exception
{
    public AgentStoppingException(Uri agentUri, Guid nodeId, Exception? innerException) : base(
        $"Failed trying to stop agent {agentUri} on node {nodeId}", innerException)
    {
    }
}