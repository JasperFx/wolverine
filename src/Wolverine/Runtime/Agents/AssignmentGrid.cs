using JasperFx.Core;

namespace Wolverine.Runtime.Agents;

/// <summary>
///     Models the desired assignment of agents between Wolverine nodes
/// </summary>
public class AssignmentGrid
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
        var node = new Node(this, wolverineNode.AssignedNodeId, wolverineNode.NodeId, wolverineNode.Capabilities);
        node.ControlUri = wolverineNode.ControlUri;
        
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
        var agents = _agents.Values.Where(x => x.Uri.Scheme.EqualsIgnoreCase(scheme)).ToList();
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
    ///     Attempts to redistribute agents for a given agent type evenly
    ///     across the known, executing nodes with minimal disruption
    /// </summary>
    /// <param name="scheme"></param>
    /// <exception cref="InvalidOperationException"></exception>
    public void DistributeEvenly(string scheme)
    {
        if (_nodes.Count == 0)
        {
            throw new InvalidOperationException("There are no active nodes");
        }

        var agents = _agents.Values.Where(x => x.Uri.Scheme.EqualsIgnoreCase(scheme)).ToList();
        if (agents.Count == 0)
        {
            return;
        }

        if (_nodes.Count == 1)
        {
            var node = _nodes.Single();
            foreach (var agent in agents) node.Assign(agent);

            return;
        }

        var spread = (double)agents.Count / _nodes.Count;
        var minimum = (int)Math.Floor(spread);
        var maximum = (int)Math.Ceiling(spread); // this is helpful to reduce the number of assignments

        // First, pair down number of running agents if necessary. Might have to steal some later
        foreach (var node in _nodes)
        {
            var extras = node.ForScheme(scheme).Skip(maximum).ToArray();
            foreach (var agent in extras) agent.Detach();
        }

        var missing = new Queue<Agent>(agents.Where(x => x.AssignedNode == null));

        // 2nd pass
        foreach (var node in _nodes)
        {
            if (missing.Count == 0)
            {
                break;
            }

            var count = node.ForScheme(scheme).Count();

            for (var i = 0; i < minimum - count; i++)
            {
                if (missing.Count == 0)
                {
                    break;
                }

                var agent = missing.Dequeue();
                node.Assign(agent);
            }
        }

        var nodesWithCapacity = _nodes.Where(x => !x.IsLeader && x.ForScheme(scheme).Count() < maximum);
        var nodeQueue = new Queue<Node>(nodesWithCapacity.ToArray());

        // Last pass for remainders
        while (missing.Count != 0)
        {
            var agent = missing.Dequeue();

            var node = _nodes.FirstOrDefault(x => !x.IsLeader && x.ForScheme(scheme).Count() < maximum) ?? _nodes.FirstOrDefault(x => !x.IsLeader) ?? _nodes.First();
            node.Assign(agent);
        }
    }

    /// <summary>
    /// Attempts to redistribute agents for a given agent type evenly
    /// across the known, executing nodes with minimal disruption. This version assumes
    /// that there is some blue/green deployment capability matching
    /// </summary>
    /// <param name="scheme"></param>
    /// <exception cref="InvalidOperationException"></exception>
    public void DistributeEvenlyWithBlueGreenSemantics(string scheme)
    {
        var nodes = _nodes;
        if (nodes.Count == 0)
        {
            throw new InvalidOperationException("There are no active nodes");
        }

        var agents = MatchAgentsToCapableNodesFor(scheme);

        if (agents.Count == 0)
        {
            return;
        }

        if (nodes.Count == 1)
        {
            var node = nodes.Single();
            foreach (var agent in agents) node.Assign(agent);

            return;
        }

        var spread = (double)agents.Count / nodes.Count;
        var minimum = (int)Math.Floor(spread);
        var maximum = (int)Math.Ceiling(spread); // this is helpful to reduce the number of assignments

        // First, pair down number of running agents if necessary. Might have to steal some later
        foreach (var node in nodes)
        {
            var extras = node.ForCurrentlyAssigned(agents).Skip(maximum).ToArray();
            foreach (var agent in extras)
            {
                agent.Detach();
            }
        }
        
        // In the missing, we're going to put the agents up top that can be supported in fewer places 
        var missing = agents.Where(x => x.AssignedNode == null).OrderBy(x => x.CandidateNodes.Count).ToList();
        foreach (var agent in missing)
        {
            // First try to find a node that has less than the minimum number of nodes
            var candidate = agent
                .CandidateNodes
                .FirstOrDefault(x => x.ForScheme(scheme).Count() < minimum) 
                            // Or fall back to the least loaded down node
                            ?? agent.CandidateNodes.MinBy(x => x.ForScheme(scheme).Count());

            candidate?.Assign(agent);
        }
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
            yield return new StopRemoteAgent(actual.Key, actual.Value);
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

    public class Agent
    {
        internal Agent(Uri uri)
        {
            Uri = uri;
            OriginalNode = null;
        }

        internal Agent(Uri uri, Node? originalNode)
        {
            Uri = uri;
            OriginalNode = originalNode;
            AssignedNode = originalNode;
        }

        public Uri Uri { get; }

        /// <summary>
        ///     The node that was executing this agent at the time the assignment
        ///     grid was being determined
        /// </summary>
        public Node? OriginalNode { get; }

        /// <summary>
        ///     The Wolverine node that will be assigned this agent when this assignment grid
        ///     is applied
        /// </summary>
        public Node? AssignedNode { get; internal set; }

        /// <summary>
        /// Possible nodes that can support this agent. NOTE: this is only applied through
        /// MatchAgentsToNodesFor()
        /// </summary>
        public List<Node> CandidateNodes { get; } = new();

        public void Detach()
        {
            if (AssignedNode == null)
            {
                return;
            }

            var active = AssignedNode;
            AssignedNode = null;
            active.Remove(this);
        }

        internal bool TryBuildAssignmentCommand(out IAgentCommand command)
        {
            command = default!;

            if (OriginalNode == null)
            {
                // Do nothing if no assignment
                if (AssignedNode == null)
                {
                    return false;
                }

                // Start the agent up for the first time on the designated node
                command = new AssignAgent(Uri, AssignedNode.ToDestination());
                return true;
            }

            if (AssignedNode == null)
            {
                // No longer assigned, so stop it where it was running
                command = new StopRemoteAgent(Uri, OriginalNode.ToDestination());
                return true;
            }

            if (AssignedNode == OriginalNode)
            {
                return false;
            }

            // reassign the agent to a different node
            command = new ReassignAgent(Uri, OriginalNode.ToDestination(), AssignedNode.ToDestination());
            return true;
        }

        public override string ToString()
        {
            return $"{nameof(Uri)}: {Uri}";
        }
    }

    public class Node
    {
        private readonly List<Agent> _agents = new();
        private readonly AssignmentGrid _parent;
        private readonly List<Uri> _capabilities;

        public Node(AssignmentGrid parent, int assignedId, Guid nodeId, List<Uri> capabilities)
        {
            _parent = parent;
            _capabilities = capabilities;
            AssignedId = assignedId;
            NodeId = nodeId;
        }

        public IReadOnlyList<Uri> Capabilities => _capabilities;

        public int AssignedId { get; }
        public Guid NodeId { get; }

        public bool IsLeader { get; internal set; }

        public IReadOnlyList<Agent> Agents => _agents;
        public Uri? ControlUri { get; set; }

        public NodeDestination ToDestination() => new NodeDestination(NodeId, ControlUri!);

        public IEnumerable<Agent> ForScheme(string agentScheme)
        {
            return _agents.Where(x => x.Uri.Scheme.EqualsIgnoreCase(agentScheme));
        }

        /// <summary>
        /// Fetch any agents that are currently assigned to this node from the supplied
        /// list of agents
        /// </summary>
        /// <param name="agents"></param>
        /// <returns></returns>
        public IEnumerable<Agent> ForCurrentlyAssigned(IEnumerable<Agent> agents)
        {
            return _agents.Intersect(agents);
        }

        public Node Running(params Uri[] agentUris)
        {
            foreach (var agentUri in agentUris)
            {
                var agent = new Agent(agentUri, this);
                _parent._agents[agentUri] = agent;

                _agents.Add(agent);
            }

            return this;
        }

        internal void Remove(Agent agent)
        {
            _agents.Remove(agent);
        }

        /// <summary>
        ///     Remove an assigned agent from this node
        /// </summary>
        /// <param name="agent"></param>
        public void Detach(Agent agent)
        {
            agent.Detach();
        }

        public bool TryAssign(Uri agentUri)
        {
            if (_capabilities.Contains(agentUri))
            {
                Assign(agentUri);
                return true;
            }

            return false;
        }

        /// <summary>
        ///     Assign a given agent to be executed on this node when the assignment grid
        ///     is applied
        /// </summary>
        /// <param name="agentUri"></param>
        public void Assign(Uri agentUri)
        {
            if (!_parent._agents.TryGetValue(agentUri, out var agent))
            {
                agent = new Agent(agentUri);
                _parent._agents[agentUri] = agent;
            }

            if (agent.AssignedNode != null)
            {
                agent.Detach();
            }

            agent.AssignedNode = this;
            _agents.Fill(agent);
        }

        /// <summary>
        ///     Assign a given agent to be executed on this node when the assignment grid
        ///     is applied
        /// </summary>
        /// <param name="agent"></param>
        public void Assign(Agent agent)
        {
            if (agent.AssignedNode != null)
            {
                agent.Detach();
            }

            agent.AssignedNode = this;
            _agents.Fill(agent);
        }

        public override string ToString()
        {
            return $"{nameof(AssignedId)}: {AssignedId}, {nameof(NodeId)}: {NodeId}";
        }
    }
}