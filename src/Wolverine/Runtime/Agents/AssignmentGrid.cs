using JasperFx.Core;

namespace Wolverine.Runtime.Agents;

public class AssignmentGrid
{
    private readonly Dictionary<Uri, Agent> _agents = new();
    private readonly List<Node> _nodes = new();

    public IReadOnlyList<Agent> UnassignedAgents => _agents.Values.Where(x => x.ActiveNode == null).ToArray();

    public IReadOnlyList<Node> Nodes => _nodes;

    public IReadOnlyList<Agent> AllAgents => _agents.Values.ToList();

    public static AssignmentGrid ForTracker(INodeStateTracker tracker)
    {
        var grid = new AssignmentGrid();
        foreach (var node in tracker.AllNodes())
        {
            var copy = grid.WithNode(node.AssignedNodeId, node.Id);
            copy.Running(node.ActiveAgents.ToArray());
            copy.IsLeader = node.IsLeader();
        }

        foreach (var agentUri in tracker.AllAgents()) grid.WithAgent(agentUri);

        return grid;
    }

    public Node WithNode(int assignedId, Guid id)
    {
        var node = new Node(this, assignedId, id);
        _nodes.Add(node);

        return node;
    }

    public AssignmentGrid WithAgents(params Uri[] uris)
    {
        foreach (var uri in uris)
        {
            if (!_agents.ContainsKey(uri))
            {
                _agents.Add(uri, new Agent(this, uri));
            }
        }

        return this;
    }

    public Agent? AgentFor(Uri agentUri)
    {
        return _agents[agentUri];
    }

    public Agent WithAgent(Uri uri)
    {
        WithAgents(uri);

        return AgentFor(uri);
    }

    public void DistributeEvenly(string scheme)
    {
        if (!_nodes.Any())
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

        var missing = new Queue<Agent>(agents.Where(x => x.ActiveNode == null));

        // 2nd pass
        foreach (var node in _nodes)
        {
            if (!missing.Any())
            {
                break;
            }

            var count = node.ForScheme(scheme).Count();

            for (var i = 0; i < minimum - count; i++)
            {
                if (!missing.Any())
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
        while (missing.Any())
        {
            var agent = missing.Dequeue();
            var node = nodeQueue.Dequeue();

            node.Assign(agent);
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

    public class Agent
    {
        private readonly AssignmentGrid _parent;

        internal Agent(AssignmentGrid parent, Uri uri)
        {
            _parent = parent;
            Uri = uri;
            OriginalNode = null;
        }

        internal Agent(AssignmentGrid parent, Uri uri, Node? originalNode)
        {
            _parent = parent;
            Uri = uri;
            OriginalNode = originalNode;
            ActiveNode = originalNode;
        }

        public Uri Uri { get; }
        public Node? OriginalNode { get; }
        public Node? ActiveNode { get; internal set; }

        public void Detach()
        {
            if (ActiveNode == null)
            {
                return;
            }

            var active = ActiveNode;
            ActiveNode = null;
            active.Remove(this);
        }

        internal bool TryBuildAssignmentCommand(out IAgentCommand command)
        {
            command = default;

            if (OriginalNode == null)
            {
                // Do nothing if no assignment
                if (ActiveNode == null)
                {
                    return false;
                }

                // Start the agent up for the first time on the designated node
                command = new AssignAgent(Uri, ActiveNode.NodeId);
                return true;
            }

            if (ActiveNode == null)
            {
                // No longer assigned, so stop it where it was running
                command = new StopRemoteAgent(Uri, OriginalNode.NodeId);
                return true;
            }

            if (ActiveNode == OriginalNode)
            {
                return false;
            }

            // reassign the agent to a different node
            command = new ReassignAgent(Uri, OriginalNode.NodeId, ActiveNode.NodeId);
            return true;
        }
    }


    public class Node
    {
        private readonly List<Agent> _agents = new();
        private readonly AssignmentGrid _parent;

        public Node(AssignmentGrid parent, int assignedId, Guid nodeId)
        {
            _parent = parent;
            AssignedId = assignedId;
            NodeId = nodeId;
        }

        public int AssignedId { get; }
        public Guid NodeId { get; }

        public bool IsLeader { get; internal set; }

        public IReadOnlyList<Agent> Agents => _agents;

        public IEnumerable<Agent> ForScheme(string agentScheme)
        {
            return _agents.Where(x => x.Uri.Scheme.EqualsIgnoreCase(agentScheme));
        }

        public Node Running(params Uri[] agentUris)
        {
            foreach (var agentUri in agentUris)
            {
                var agent = new Agent(_parent, agentUri, this);
                _parent._agents[agentUri] = agent;

                _agents.Add(agent);
            }

            return this;
        }

        internal void Remove(Agent agent)
        {
            _agents.Remove(agent);
        }

        public void Detach(Agent agent)
        {
            agent.Detach();
        }

        public void Assign(Uri agentUri)
        {
            if (!_parent._agents.TryGetValue(agentUri, out var agent))
            {
                agent = new Agent(_parent, agentUri);
                _parent._agents[agentUri] = agent;
            }

            if (agent.ActiveNode != null)
            {
                agent.Detach();
            }

            agent.ActiveNode = this;
            _agents.Fill(agent);
        }

        public void Assign(Agent agent)
        {
            if (agent.ActiveNode != null)
            {
                agent.Detach();
            }

            agent.ActiveNode = this;
            _agents.Fill(agent);
        }
    }
}