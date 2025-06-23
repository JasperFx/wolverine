using JasperFx.Core;

namespace Wolverine.Runtime.Agents;

public partial class AssignmentGrid
{
    public class Node
    {
        private readonly List<Agent> _agents = new();
        private readonly AssignmentGrid _parent;
        private readonly List<Uri> _capabilities;

        public Node(AssignmentGrid parent, int assignedId, Guid nodeId, List<Uri> capabilities)
        {
            _parent = parent;
            
            // It's important to order here
            _capabilities = capabilities.OrderBy(x => x.ToString()).ToList();
            AssignedId = assignedId;
            NodeId = nodeId;
        }

        public IReadOnlyList<Uri> Capabilities => _capabilities;

        /// <summary>
        /// Helping tester to add capabilities to each node
        /// </summary>
        /// <param name="agentUris"></param>
        /// <returns></returns>
        public Node HasCapabilities(IEnumerable<Uri> agentUris)
        {
            _capabilities.Fill(agentUris);
            return this;
        }

        public IReadOnlyList<Uri> OrderedCapabilitiesForScheme(string scheme) => _capabilities
            .Where(x => x.Scheme.EqualsIgnoreCase(scheme))
            .OrderBy(x => x.ToString())
            .ToList();

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