namespace Wolverine.Runtime.Agents;

public partial class AssignmentGrid
{
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
        /// Does this agent have a "pinning" assignment rule
        /// to a specific node?
        /// </summary>
        public bool IsPinned { get; set; }
        
        /// <summary>
        /// Is this node purposely paused within the application?
        /// </summary>
        public bool IsPaused { get; set; }

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
}