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

        private HashSet<Uri>? _capabilityLookup;

        /// <summary>
        ///     Whether this node advertised the given agent. A node in a fleet running thousands of
        ///     projection agents carries thousands of capabilities, so this is asked through a set built
        ///     once per grid rather than by scanning the list.
        /// </summary>
        internal bool Declares(Uri agentUri)
        {
            return (_capabilityLookup ??= _capabilities.ToHashSet()).Contains(agentUri);
        }

        /// <summary>
        /// Helping tester to add capabilities to each node
        /// </summary>
        /// <param name="agentUris"></param>
        /// <returns></returns>
        public Node HasCapabilities(IEnumerable<Uri> agentUris)
        {
            _capabilities.Fill(agentUris);
            _capabilityLookup = null;
            return this;
        }

        public IReadOnlyList<Uri> OrderedCapabilitiesForScheme(string scheme) => _capabilities
            .Where(x => x.Scheme.EqualsIgnoreCase(scheme))
            .OrderBy(x => x.ToString())
            .ToList();

        public int AssignedId { get; }
        public Guid NodeId { get; }

        public bool IsLeader { get; internal set; }

        /// <summary>
        ///     The load percentage this node advertised on its last heartbeat, or null when it isn't
        ///     advertising load. See <see cref="WolverineNode.LoadFactor" />.
        /// </summary>
        public double? LoadFactor { get; internal set; }

        /// <summary>
        ///     Whether the leader considers this node overloaded for this evaluation
        ///     (<see cref="LoadFactor" /> at or above
        ///     <see cref="DurabilitySettings.NodeOverloadThreshold" />): the distribution methods shed
        ///     agents off it. Never true unless capacity-aware assignment is enabled.
        /// </summary>
        public bool IsOverloaded { get; internal set; }

        /// <summary>
        ///     Whether this node may receive new agent placements this evaluation. Sits a hysteresis
        ///     band below the shed line, so a node between the two thresholds neither sheds nor
        ///     receives. Always true when capacity-aware assignment is off.
        /// </summary>
        public bool IsAcceptingAgents { get; internal set; } = true;

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

        /// <summary>
        ///     The agents this node must give up to come down to <paramref name="ceiling" /> for the
        ///     pass described by <paramref name="belongsToThisPass" />, never including a pinned one.
        /// </summary>
        /// <remarks>
        ///     GH-4591. Restrictions are applied (AssignmentGrid.ApplyRestrictions) BEFORE the families
        ///     distribute, so a ceiling pass that detached whatever sat above the line would undo an
        ///     operator's pin -- and ApplyRestrictions would re-apply it on the next evaluation, and the
        ///     pass would undo it again: a churn loop that emits commands forever and never converges,
        ///     for as long as the pin sits on a node above its share.
        ///
        ///     <para>Pins still COUNT toward the ceiling, so a node carrying them gives up more of its
        ///     unpinned agents instead of exceeding its share. A node whose pins alone reach the ceiling
        ///     gives up every unpinned agent and stops there — that is the pin doing exactly what it was
        ///     asked to do.</para>
        /// </remarks>
        internal Agent[] ExtrasAboveCeiling(Func<Agent, bool> belongsToThisPass, int ceiling)
        {
            var mine = _agents.Where(belongsToThisPass).ToList();
            var pinned = mine.Count(x => x.IsPinned);

            return mine
                .Where(x => !x.IsPinned)
                .Skip(Math.Max(0, ceiling - pinned))
                .ToArray();
        }

        public Node Running(params Uri[] agentUris)
        {
            foreach (var agentUri in agentUris)
            {
                // Detect split-brain residue: another node already reported
                // this agent as running, so we have a duplicate. Record it so
                // the leader can emit a StopRemoteAgent against the existing
                // copy. Without this, the dictionary write below silently
                // overwrites the first node's entry and the duplicate becomes
                // invisible to FindDelta. See GH-2602.
                if (_parent._agents.TryGetValue(agentUri, out var existing) &&
                    existing.OriginalNode != null &&
                    !ReferenceEquals(existing.OriginalNode, this))
                {
                    _parent.RecordDuplicateAgent(agentUri, existing.OriginalNode, this);
                }

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
            if (Declares(agentUri))
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