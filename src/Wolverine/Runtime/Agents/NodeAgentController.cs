using System.Threading.Tasks.Dataflow;
using JasperFx.Core;
using Microsoft.Extensions.Logging;
using Wolverine.Runtime.WorkerQueues;
using Wolverine.Util.Dataflow;

namespace Wolverine.Runtime.Agents;



internal interface IInternalMessage{}

public record StartLocalAgentProcessing(WolverineOptions Options) : IInternalMessage;

public record EvaluateAssignments : IInternalMessage;

public record CheckAgentHealth : IInternalMessage;



public class NodeAgentController : IInternalHandler<StartLocalAgentProcessing>
    , IInternalHandler<TryAssumeLeadership>
    , IInternalHandler<NodeEvent>
    , IInternalHandler<EvaluateAssignments>
    , IInternalHandler<CheckAgentHealth>
{
    public static readonly Uri LeaderUri = new("wolverine://leader");

    private readonly IWolverineRuntime _runtime;
    private readonly INodeStateTracker _tracker;
    private readonly INodeAgentPersistence _persistence;

    private readonly Dictionary<string, IAgentController>
        _agentControllers = new();
    private readonly CancellationToken _cancellation;
    private readonly ILogger _logger;

    internal NodeAgentController(IWolverineRuntime runtime, INodeStateTracker tracker, INodeAgentPersistence persistence,
        IEnumerable<IAgentController> agentControllers, ILogger logger, CancellationToken cancellation)
    {
        _runtime = runtime;
        _tracker = tracker;
        _persistence = persistence;
        foreach (var agentController in agentControllers)
        {
            _agentControllers[agentController.Scheme] = agentController;
        }
        _cancellation = cancellation;
        _logger = logger;
        
        _assignmentBlock = new ActionBlock<EvaluateAssignments[]>(async batch =>
        {
            await new MessageBus(runtime).PublishAsync(new EvaluateAssignments());
        }, new ExecutionDataflowBlockOptions{CancellationToken = runtime.Cancellation});

        _assignmentBufferBlock = new BatchingBlock<EvaluateAssignments>(runtime.Options.Durability.EvaluateAssignmentBufferTime, _assignmentBlock);
        
    }
    
    public async IAsyncEnumerable<object> HandleAsync(StartLocalAgentProcessing command)
    {
        _logger.LogInformation("Starting agents for Node {NodeId}", command.Options.UniqueNodeId);
        var others = await _persistence.LoadAllNodesAsync(_cancellation);

        var current = WolverineNode.For(command.Options);
        
        current.AssignedNodeId = await _persistence.PersistAsync(current, _cancellation);
        foreach (var controller in _agentControllers.Values)
        {
            current.Capabilities.AddRange(await controller.SupportedAgentsAsync());
        }
        
        _tracker.MarkCurrent(current);
        
        if (others.Any())
        {
            foreach (var other in others)
            {
                var active = _tracker.Add(other);
                yield return new NodeEvent(current, NodeEventType.Started).ToNode(active);
            }

            if (_tracker.Leader == null)
            {
                // Find the oldest, ask it to assume leadership
                var leaderCandidate = others.MinBy(x => x.AssignedNodeId) ?? current;
                
                _logger.LogInformation("Found no elected leader on node startup, requesting node {NodeId} to be the new leader", leaderCandidate.Id);
                
                yield return new TryAssumeLeadership{CurrentLeaderId = null}.ToNode(leaderCandidate);
            }
        }
        else
        {
            _logger.LogInformation("Found no other existing nodes, deciding to assume leadership in node {NodeId}", command.Options.UniqueNodeId);
            
            // send local command
            yield return new TryAssumeLeadership{CurrentLeaderId = null};
        }
    }
    
    public async IAsyncEnumerable<object> HandleAsync(TryAssumeLeadership command)
    {
        if (_tracker.Self.IsLeader())
        {
            _logger.LogInformation("Already the current leader ({NodeId}), ignoring the request to assume leadership", _tracker.Self.Id);
            yield break;
        }
        
        var assigned = await _persistence.MarkNodeAsLeaderAsync(command.CurrentLeaderId, _tracker.Self!.Id);

        if (assigned.HasValue)
        {
            if (assigned == _tracker.Self.Id)
            {
                _logger.LogInformation("Node {NodeId} successfully assumed leadership", _tracker.Self.Id);

                var all = await _persistence.LoadAllNodesAsync(_cancellation);
                var others = all.Where(x => x.Id != _tracker.Self.Id).ToArray();
                foreach (var other in others)
                {
                    _tracker.Add(other);
                    yield return new NodeEvent(_tracker.Self, NodeEventType.LeadershipAssumed).ToNode(other);
                }

                _tracker.Publish(new NodeEvent(_tracker.Self, NodeEventType.LeadershipAssumed));

                foreach (var controller in _agentControllers.Values)
                {
                    var agents = await controller.AllKnownAgentsAsync();
                    _tracker.RegisterAgents(agents);
                }

                await requestAssignmentEvaluation();
            }
            else
            {
                var leader = await _persistence.LoadNodeAsync(assigned.Value, _cancellation);

                if (leader != null)
                {
                    _logger.LogInformation("Tried to assume leadership at node {NodeId}, but another node {LeaderId} has assumed leadership beforehand", _tracker.Self.Id, assigned.Value);
                    _tracker.Publish(new NodeEvent(leader, NodeEventType.LeadershipAssumed));
                }
                else
                {
                    // The referenced leader doesn't exist -- which shouldn't happen, but real life, so try again...
                    yield return new TryAssumeLeadership();
                }

            }

            yield break;
        }

        _logger.LogInformation("Node {NodeId} was unable to assume leadership, and no leader was found", _tracker.Self.Id);
        
        // Try it again
        yield return new TryAssumeLeadership();
    }

    private async Task requestAssignmentEvaluation()
    {
        await _assignmentBufferBlock.SendAsync(new EvaluateAssignments());
    }

    // Tested w/ integration tests all the way
    public async IAsyncEnumerable<object> HandleAsync(EvaluateAssignments command)
    {
        var grid = AssignmentGrid.ForTracker(_tracker);

        foreach (var controller in _agentControllers.Values)
        {
            try
            {
                var allAgents = await controller.AllKnownAgentsAsync();
                grid.WithAgents(allAgents.ToArray()); // Just in case something has gotten lost, and this is master anyway

                await controller.EvaluateAssignmentsAsync(grid);
            }
            catch (Exception e)
            {
                _logger.LogError(e, "Error trying to reevaluate agent assignments for '{Scheme}' agents", controller.Scheme);
            }
        }

        var commands = new List<IAgentCommand>();

        foreach (var agent in grid.AllAgents)
        {
            if (agent.TryBuildAssignmentCommand(out var agentCommand))
            {
                commands.Add(agentCommand);
            }
        }   
        
        _tracker.Publish(new AgentAssignmentsChanged(commands, _tracker.AllNodes().Select(x => x.AssignedNodeId).ToArray()));

        batchCommands(commands);
        
        // TODO -- try to batch the commands
        // TODO -- raise events on agent start up failures

        foreach (var agentCommand in commands)
        {
            yield return agentCommand;
        }
    }

    private static void batchCommands(List<IAgentCommand> commands)
    {
        foreach (var group in commands.OfType<AssignAgent>().GroupBy(x => x.NodeId).Where(x => x.Count() > 1).ToArray())
        {
            var assignAgents = new AssignAgents(group.Key, group.Select(x => x.AgentUri).ToArray());

            foreach (var message in group)
            {
                commands.Remove(message);
            }

            commands.Add(assignAgents);
        }
        
        foreach (var group in commands.OfType<StopRemoteAgent>().GroupBy(x => x.NodeId).Where(x => x.Count() > 1).ToArray())
        {
            var stopAgents = new StopRemoteAgents(group.Key, group.Select(x => x.AgentUri).ToArray());

            foreach (var message in group)
            {
                commands.Remove(message);
            }

            commands.Add(stopAgents);
        }
    }

    // Do assignments one by one, agent by agent
    public async IAsyncEnumerable<object> HandleAsync(NodeEvent @event)
    {
        _logger.LogInformation("Processing node event {Type} from node {OtherId} in node {NodeId}", @event.Node.Id, @event.Type, _tracker.Self.Id);
        
        switch (@event.Type)
        {
            case NodeEventType.Exiting:
                _tracker.Remove(@event.Node);

                if (_tracker.Self.IsLeader())
                {
                    await _persistence.DeleteAsync(@event.Node.Id);
                    await requestAssignmentEvaluation();
                }
                else if (_tracker.Leader == null || _tracker.Leader.Id == @event.Node.Id)
                {
                    var candidate = _tracker.OtherNodes().MinBy(x => x.AssignedNodeId);

                    if (candidate == null || candidate.AssignedNodeId > _tracker.Self.AssignedNodeId)
                    {
                        yield return new TryAssumeLeadership();
                    }
                    else
                    {
                        yield return new TryAssumeLeadership().ToNode(candidate);
                    }
                }

                break;
                
            case NodeEventType.Started:
                _tracker.Add(@event.Node);
                if (_tracker.Self.IsLeader())
                {
                    await requestAssignmentEvaluation();
                }

                break;
                    

            case NodeEventType.LeadershipAssumed:
                // Nothing actually, because publishing the event to the tracker will
                // happily change the state
                break;
            
            default:
                throw new ArgumentOutOfRangeException(nameof(@event));
        }
        
        // If the call above succeeded, this is low risk
        _tracker.Publish(@event);
    }

    public async IAsyncEnumerable<object> HandleAsync(CheckAgentHealth message)
    {
        if (_cancellation.IsCancellationRequested) yield break;
        if (_tracker.Self == null) yield break;
        
        // write health check regardless
        await _persistence.MarkHealthCheckAsync(_tracker.Self.Id);
        
        if (_tracker.Self.IsLeader())
        {
            // check health of each node. 
            // verify that all agents are assigned? Run assignment through the agents? How do we know when to trip off?
        }
        else
        {
            // potentially trigger leadership if leader appears to be offline
        }

        yield break;
    }

    internal void AddHandlers(WolverineRuntime runtime)
    {
        var handlers = runtime.Handlers;
        handlers.AddMessageHandler(typeof(NodeEvent),new InternalMessageHandler<NodeEvent>(this));
        handlers.AddMessageHandler(typeof(StartLocalAgentProcessing),new InternalMessageHandler<StartLocalAgentProcessing>(this));
        handlers.AddMessageHandler(typeof(EvaluateAssignments),new InternalMessageHandler<EvaluateAssignments>(this));
        handlers.AddMessageHandler(typeof(TryAssumeLeadership),new InternalMessageHandler<TryAssumeLeadership>(this));
        handlers.AddMessageHandler(typeof(CheckAgentHealth),new InternalMessageHandler<CheckAgentHealth>(this));
        
        handlers.AddMessageHandler(typeof(IAgentCommand), new AgentCommandHandler(runtime));
        
        handlers.RegisterMessageType(typeof(StartAgent));
        handlers.RegisterMessageType(typeof(StartAgents));
        handlers.RegisterMessageType(typeof(AgentsStarted));
        handlers.RegisterMessageType(typeof(AgentsStopped));
        handlers.RegisterMessageType(typeof(StopAgent));
        handlers.RegisterMessageType(typeof(StopAgents));

    }

    public async Task StopAsync(IMessageBus messageBus)
    {
        foreach (var entry in _agents.Enumerate())
        {
            try
            {
                await entry.Value.StartAsync(CancellationToken.None);
            }
            catch (Exception e)
            {
                _logger.LogError(e, "Error trying to stop agent {AgentUri}", entry.Value.Uri);
            }
        }
        
        try
        {
            if (_tracker.Self.IsLeader())
            {
                // notify everyone
                // Don't trust the in memory storage of nodes, fetch from storage
                var controlUris = await _persistence.LoadAllOtherNodeControlUrisAsync(_tracker.Self.Id);
                foreach (var uri in controlUris)
                {
                    await messageBus.EndpointFor(uri).SendAsync(new NodeEvent(_tracker.Self, NodeEventType.Exiting));
                }
            }
            else
            {
                // Don't trust the in memory storage of nodes, fetch from storage
                // ONLY notify the leader. Makes tests work better:)
                var controlUri = await _persistence.FindLeaderControlUriAsync(_tracker.Self.Id);
            
                if (controlUri != null)
                {
                    await messageBus.EndpointFor(controlUri).SendAsync(new NodeEvent(_tracker.Self, NodeEventType.Exiting));
                }
            }
            

        }
        catch (Exception e)
        {
            _logger.LogError(e, "Error trying to notify other nodes about this node exiting");
        }

        try
        {
            await _persistence.DeleteAsync(_tracker.Self.Id);
        }
        catch (Exception e)
        {
            _logger.LogError(e, "Error trying to delete the exiting node from node persistence");
        }
    }

    private ImHashMap<Uri, IAgent> _agents = ImHashMap<Uri, IAgent>.Empty;
    private readonly ActionBlock<EvaluateAssignments[]> _assignmentBlock;
    private readonly BatchingBlock<EvaluateAssignments> _assignmentBufferBlock;

    private ValueTask<IAgent> findAgentAsync(Uri uri)
    {
        if (_agentControllers.TryGetValue(uri.Scheme, out var controller))
        {
            return controller.BuildAgentAsync(uri);
        }

        throw new ArgumentOutOfRangeException(nameof(uri), $"Unrecognized agent scheme '{uri.Scheme}'");
    }

    public async Task StartAgentAsync(Uri agentUri)
    {
        if (_agents.Contains(agentUri)) return;

        var agent = await findAgentAsync(agentUri);
        await agent.StartAsync(_cancellation);

        _agents = _agents.AddOrUpdate(agentUri, agent);

        try
        {
            await _persistence.AddAssignmentAsync(_runtime.Options.UniqueNodeId, agentUri, _cancellation);
        }
        catch (Exception e)
        {
            _logger.LogError(e, "Error trying to persist the assignment of agent {AgentUri} to Node {NodeId}", agentUri, _runtime.Options.UniqueNodeId);
        }
    }

    public async Task StopAgentAsync(Uri agentUri)
    {
        _assignmentBufferBlock.Complete();
        _assignmentBlock.Complete();
        
        if (_agents.TryFind(agentUri, out var agent))
        {
            await agent.StopAsync(_cancellation);
            _agents = _agents.Remove(agentUri);
            
            try
            {
                await _persistence.RemoveAssignmentAsync(_runtime.Options.UniqueNodeId, agentUri, _cancellation);
            }
            catch (Exception e)
            {
                _logger.LogError(e, "Error trying to remove the assignment of agent {AgentUri} to Node {NodeId} in persistence", agentUri, _runtime.Options.UniqueNodeId);
            }
        }
    }
    
    
}