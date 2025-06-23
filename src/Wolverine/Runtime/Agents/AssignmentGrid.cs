using System;
using System.Collections.Generic;
using System.Linq;
using JasperFx.Core;

namespace Wolverine.Runtime.Agents;


/// <summary>
///     Models the desired assignment of agents between Wolverine nodes
/// </summary>
public partial class AssignmentGrid
{
    private readonly Dictionary<Uri, Agent> _agents = new();
    private readonly List<Node> _nodes = new();

    /// <summary>
    ///     The identity of all currently unassigned agents
    /// </summary>
    public IReadOnlyList<Agent> UnassignedAgents => _agents.Values.Where(x => x.AssignedNode == null).ToArray();

    /// <summary>
    ///     Identity and location of all the currently executing nodes for this Wolverine application
    /// </summary>
    public IReadOnlyList<Node> Nodes => _nodes;

    /// <summary>
    ///     All agents, including a model of their current and intended node assignments
    /// </summary>
    public IReadOnlyList<Agent> AllAgents => _agents.Values.ToList();

    /// <summary>
    ///     Timestamp when this assignment grid was last updated
    /// </summary>
    public DateTimeOffset EvaluationTime { get; internal set; }

    public IReadOnlyList<Agent> AgentsForScheme(string scheme)
    {
        return _agents.Values.Where(x => x.Uri.Scheme.EqualsIgnoreCase(scheme)).ToList();
    }
    
    public IReadOnlyList<Agent> AvailableAgentsForScheme(string scheme)
    {
        return _agents.Values.Where(x => x.Uri.Scheme.EqualsIgnoreCase(scheme) && !x.IsPaused).ToList();
    }

    /// <summary>
    ///     Add an executing node to the grid. Useful for testing custom agent assignment
    ///     schemes
    /// </summary>
    /// <param name="assignedId"></param>
    /// <param name="id"></param>
    /// <param name="capabilities"></param>
    /// <returns></returns>
    public Node WithNode(WolverineNode wolverineNode)
    {
        var node = new Node(this, wolverineNode.AssignedNodeNumber, wolverineNode.NodeId, wolverineNode.Capabilities);
        node.ControlUri = wolverineNode.ControlUri;

        node.IsLeader = wolverineNode.ActiveAgents.Contains(NodeAgentController.LeaderUri);
        
        _nodes.Add(node);

        node.Running(wolverineNode.ActiveAgents.ToArray());
        
        return node;
    }

    public Node WithNode(int assignedId, Guid id)
    {
        var node = new Node(this, assignedId, id, new List<Uri>());
        _nodes.Add(node);

        return node;
    }

    /// <summary>
    ///     Add additional agent uris to this assignment grid. This is intended for
    ///     testing scenarios
    /// </summary>
    /// <param name="uris"></param>
    /// <returns></returns>
    public AssignmentGrid WithAgents(params Uri[] uris)
    {
        foreach (var uri in uris)
        {
            if (!_agents.ContainsKey(uri))
            {
                _agents.Add(uri, new Agent(uri));
            }
        }

        return this;
    }

    /// <summary>
    /// Match up the agents for a particular scheme to any nodes that could
    /// run that agent. This is meant for blue/green development
    /// </summary>
    /// <param name="scheme"></param>
    /// <returns></returns>
    public IReadOnlyList<Agent> MatchAgentsToCapableNodesFor(string scheme)
    {
        var agents = AvailableAgentsForScheme(scheme);
        foreach (var agent in agents)
        {
            agent.CandidateNodes.Clear();
            agent.CandidateNodes.AddRange(_nodes.Where(x => x.Capabilities.Contains(agent.Uri)).OrderBy(x => x.AssignedId));
        }

        return agents;
    }

    /// <summary>
    ///     Find information about a single agent by its Uri
    /// </summary>
    /// <param name="agentUri"></param>
    /// <returns></returns>
    public Agent AgentFor(Uri agentUri)
    {
        return _agents[agentUri];
    }

    public Node? NodeFor(int nodeNumber)
    {
        return _nodes.FirstOrDefault(x => x.AssignedId == nodeNumber);
    }

    /// <summary>
    ///     Testing helper, adds a new agent to the current assignment grid
    /// </summary>
    /// <param name="uri"></param>
    /// <returns></returns>
    public Agent WithAgent(Uri uri)
    {
        WithAgents(uri);

        return AgentFor(uri);
    }


    /// <summary>
    ///     Probably just for testing to simulate nodes going down
    /// </summary>
    /// <param name="node"></param>
    public void Remove(Node node)
    {
        foreach (var agent in node.Agents.ToArray()) agent.Detach();

        _nodes.Remove(node);
    }

    /// <summary>
    ///     The point of this is to find any agents that are currently misaligned with the
    ///     expected assignments
    /// </summary>
    /// <param name="actuals"></param>
    /// <returns></returns>
    public IEnumerable<IAgentCommand> FindDelta(Dictionary<Uri, NodeDestination> actuals)
    {
        foreach (var assignment in _agents)
        {
            if (assignment.Key.Scheme == "wolverine")
            {
                continue;
            }

            if (actuals.TryGetValue(assignment.Key, out var actual))
            {
                var specified = assignment.Value.AssignedNode?.ToDestination();
                if (specified == null)
                {
                    // Should not be running, so stop it
                    yield return new StopRemoteAgent(assignment.Key, actual);
                }
                else if (actual != specified)
                {
                    // Running in wrong place, reassign to correct place
                    yield return new ReassignAgent(assignment.Key, actual, specified);
                }
            }
            else if (assignment.Value.AssignedNode != null)
            {
                // Missing, so add it
                yield return new AssignAgent(assignment.Key, assignment.Value.AssignedNode.ToDestination());
            }
        }

        foreach (var actual in actuals.Where(pair => !_agents.ContainsKey(pair.Key)))
        {
            yield return new StopRemoteAgent(actual.Key, actual.Value);
        }
    }

    internal Dictionary<Uri, NodeDestination> CompileAssignments()
    {
        var dict = new Dictionary<Uri, NodeDestination>();
        foreach (var pair in _agents)
        {
            if (pair.Value.AssignedNode != null)
            {
                dict.Add(pair.Key, pair.Value.AssignedNode.ToDestination());
            }
        }

        return dict;
    }

    public void RunOnLeader(Uri agentUri)
    {
        var node = _nodes.FirstOrDefault(x => x.IsLeader);
        node?.Assign(agentUri);
    }

    public void ApplyRestrictions(AgentRestrictions restrictions)
    {
        // Assume that the AssignmentGrid is completely fleshed out with the 
        // known agents and nodes at this point
        foreach (var pin in restrictions.Pins())
        {
            var agent = AgentFor(pin.AgentUri);
            if (agent == null) continue;
            
            var node = NodeFor(pin.NodeNumber);

            if (node == null)
            {
                restrictions.RemovePin(pin.AgentUri);
            }
            else
            {
                if (node.TryAssign(pin.AgentUri))
                {
                    agent.IsPinned = true;
                }
            }
        }

        foreach (var agentUri in restrictions.FindPausedAgentUris())
        {
            var agent = AgentFor(agentUri);
            if (agent == null) continue;

            agent.IsPaused = true;
            if (agent.AssignedNode != null)
            {
                agent.Detach();
            }
        }
        
    }
}