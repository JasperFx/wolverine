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
        ///     GH-3698. The node the leader has already dispatched a start to, but which has not yet been
        ///     confirmed running it. Distinct from <see cref="OriginalNode" />, which is only ever populated
        ///     from a node's <i>persisted</i> active agents and therefore lags a dispatch by however long the
        ///     destination takes to start the agent and write its assignment row — many evaluation cycles for
        ///     a node bringing up thousands of subscription agents.
        ///
        ///     <para>Without this, such an agent looks entirely unplaced to the grid, and moving it produced a
        ///     bare <see cref="AssignAgent" /> to a second node with no stop for the copy already starting on
        ///     the first: the same agent running twice. Populated by the leader from its pending-assignment
        ///     ledger; see <c>NodeAgentController.applyPendingAssignments</c>.</para>
        ///
        ///     <para>GH-3852: also set for an agent that IS running somewhere and is being moved off it, where
        ///     <see cref="OriginalNode" /> is the source the agent has not been observed leaving yet. That case
        ///     needs no stop synthesised — <see cref="ReassignAgent" /> already carries one — so what this
        ///     buys there is purely that the leader stops re-deciding a move it has already dispatched.</para>
        /// </summary>
        internal Node? PendingNode { get; set; }

        /// <summary>
        ///     GH-3698. The dispatch to <see cref="PendingNode" /> is no longer outstanding — its lane has
        ///     finished with the command, one way or the other — and the agent still has not shown up as
        ///     running there, so the leader should drive the start again. The agent stays <i>recorded</i>
        ///     against that node either way: a command that timed out on the wire may well still be starting
        ///     agents at the other end, so re-driving it must never take the form of a bare start elsewhere.
        /// </summary>
        internal bool PendingRetryDue { get; set; }

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
                // GH-3698: not running anywhere yet, but a start is already on its way to PendingNode. Every
                // outcome here has to account for the copy that may be about to come up there -- a bare
                // AssignAgent elsewhere, or silence, leaves it running unsupervised.
                if (PendingNode != null)
                {
                    if (AssignedNode == null)
                    {
                        // Detached since we dispatched -- an operator pause, or the agent leaving the family.
                        // Stop the pending copy. This lands in PendingNode's lane, so it is ordered behind the
                        // start still queued there rather than racing it.
                        command = new StopRemoteAgent(Uri, PendingNode.ToDestination());
                        return true;
                    }

                    if (AssignedNode == PendingNode)
                    {
                        // Still going to the same place. Re-emitting while the dispatch is live would pile a
                        // duplicate onto the control queue and a duplicate AssignmentChanged row behind it --
                        // GH-3604/D6, from a node simply being slow. Once it has gone unconfirmed for long
                        // enough, drive it again.
                        if (!PendingRetryDue)
                        {
                            return false;
                        }

                        command = new AssignAgent(Uri, AssignedNode.ToDestination());
                        return true;
                    }

                    // Being moved before the first start was confirmed. Stop-then-start, sequenced through
                    // the pending node's lane, so whichever of the two nodes ends up with it, only one does.
                    // ReassignAgent always runs in its source's lane (GH-3749), which is exactly the ordering
                    // this case needs: the stop queues behind the start it is cancelling.
                    command = new ReassignAgent(Uri, PendingNode.ToDestination(), AssignedNode.ToDestination());
                    return true;
                }

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

            // GH-3852: this move is one the leader already dispatched and which is still live. Re-emitting it
            // costs an AssignmentChanged row per agent per evaluation cycle -- 3,468 of them per cycle on a
            // 512-database cluster mid-ramp -- and, once the dispatcher's in-flight hold on the previous copy
            // has been released, a redundant StopAgents round trip against a source that already let go.
            // The agent stays where the ledger put it either way; only the command is suppressed. Once the
            // dispatch stops being outstanding without the agent turning up there, PendingRetryDue re-drives
            // it as a fresh reassignment from wherever it is actually running now.
            if (AssignedNode == PendingNode && !PendingRetryDue)
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