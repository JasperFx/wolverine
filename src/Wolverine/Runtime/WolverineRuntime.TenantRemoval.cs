using JasperFx.MultiTenancy;
using Microsoft.Extensions.Logging;
using Wolverine.Persistence.Durability;
using Wolverine.Runtime.Agents;
using Wolverine.Transports;

namespace Wolverine.Runtime;

// GH-5840. Taking a removed tenant's database out of a running node, for an operator tool (CritterWatch) to call
// on every node of the fleet before the tenancy entry, the database and its role are removed. Internal by design
// (GH-4672): this is operator plumbing, not an application API.
public partial class WolverineRuntime
{
    /// <summary>
    ///     Deregister one tenant from this node: stop the durability agent of its database, forget its message store
    ///     and stop any per-tenant queue listener on it, so nothing on this node polls that database again. In
    ///     Balanced mode the leader is told as well, and drops the agent from the assignment for good.
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         Idempotent, and safe to call on every node in any order. A database another live tenant still uses is
    ///         left running; only the tenant's mapping to it goes.
    ///     </para>
    ///     <para>
    ///         The removal lasts for the life of the process. Using the tenant afterward throws
    ///         <see cref="RemovedTenantException" />. Adding it again through Wolverine's own master-table
    ///         <c>AddTenantAsync</c> readmits it on the node that does the add; a tenant re-added only through
    ///         Marten's or Polecat's own tenancy is not seen until the process restarts.
    ///     </para>
    ///     <para>Check <see cref="FindPendingWorkForTenantAsync" /> first: envelopes still in the tenant's database stay there.</para>
    /// </remarks>
    internal async Task<TenantDeregistration> DeregisterTenantAsync(string tenantId, CancellationToken cancellation = default)
    {
        if (Options.Durability.Mode != DurabilityMode.Balanced || NodeController == null || NodeController.IsLeader)
        {
            var local = await deregisterTenantLocallyAsync(tenantId);
            local.LeaderNotified = Options.Durability.Mode == DurabilityMode.Balanced && NodeController != null;
            return local;
        }

        // Option (ii) of GH-5840: whichever node this runs on, the leader hears about it, because the leader is the
        // one that would otherwise put the agent straight back on some node. The leader goes FIRST: once it has
        // dropped the agent, nothing places it on this node while this node is taking the database out. Safe to
        // send any number of times.
        var leader = await findLeaderAsync(cancellation);
        if (leader != null)
        {
            await InvokeAsync(leader, new DeregisterTenantOnLeader(tenantId, []));
        }
        else
        {
            Logger.LogWarning(
                "No leader node found to drop the durability agent of removed tenant {TenantId}. The leader drops it when this is called on the leader node",
                tenantId);
        }

        var result = await deregisterTenantLocallyAsync(tenantId);

        // In case the leader's view of the tenant's databases is not this node's
        if (leader != null && result.DeregisteredStores.Count > 0)
        {
            await InvokeAsync(leader, new DeregisterTenantOnLeader(tenantId, result.DeregisteredStores.ToArray()));
        }

        result.LeaderNotified = leader != null;
        return result;
    }

    private async Task<NodeDestination?> findLeaderAsync(CancellationToken cancellation)
    {
        var nodes = await Storage.Nodes.LoadAllNodesAsync(cancellation);
        var leader = nodes.FirstOrDefault(x => x.ActiveAgents.Contains(NodeAgentController.LeaderUri));
        return leader?.ControlUri == null ? null : new NodeDestination(leader.NodeId, leader.ControlUri);
    }

    private async Task<TenantDeregistration> deregisterTenantLocallyAsync(string tenantId)
    {
        tenantId = Options.Durability.TenantIdStyle.MaybeCorrectTenantId(tenantId);
        var result = new TenantDeregistration(tenantId, Options.Durability.AssignedNodeNumber, IsLeader());

        // Conjoined tenancy and single databases have no per-tenant store to take out
        if (!Stores.MultiTenanted.Any()) return result;

        // First, so no refresh re-adds the tenant and no lookup rebuilds its store from here on
        Options.RemovedTenants.Add(tenantId);

        foreach (var tenanted in Stores.MultiTenanted)
        {
            if (tenanted.Source is not ITenantStoreRemoval source) continue;

            var removal = source.RemoveTenant(tenantId);

            // A tenant can point at the main database. That one stays, agent and all.
            var orphaned = removal.Orphaned.Where(x => !Stores.IsMainDatabase(x)).ToArray();
            result.SharedStoresKept.AddRange(removal.Shared.Concat(removal.Orphaned.Except(orphaned))
                .Concat(removal.Detached.Select(x => x.Store)).Select(x => x.Uri));

            // 1. Stop the durability agent before anything is disposed under it. StopAgentAsync waits out the
            // agent's own shutdown, so a durability pass in flight finishes before the store goes.
            var running = AllRunningAgentUris();
            foreach (var store in orphaned)
            {
                if (NodeController != null && running.Contains(store.Uri))
                {
                    await NodeController.StopAgentAsync(store.Uri);
                    result.StoppedAgents.Add(store.Uri);
                }
            }

            // A store leaving while another still covers its database: if the database's agent runs through it,
            // move the database over to the other store and restart the agent there
            foreach (var detached in removal.Detached)
            {
                var uri = detached.Store.Uri;
                var restart = NodeController != null && running.Contains(uri) && Stores.IsRegistered(detached.Store);
                if (restart) await NodeController!.StopAgentAsync(uri);

                Stores.ReplaceRegistration(detached.Store, detached.Replacement);

                // Balanced: the leader places it again, the node's assignment having gone with the stop
                if (restart && Options.Durability.Mode == DurabilityMode.Solo) await NodeController!.StartAgentAsync(uri);
            }

            var leaving = orphaned.Concat(removal.Detached.Select(x => x.Store)).ToArray();

            // 2. Nothing hands out the stores any more
            tenanted.EvictTenant(tenantId, leaving);

            // 3. Stop the per-tenant listeners, then dispose the store
            foreach (var store in leaving)
            {
                var replacement = removal.Detached.FirstOrDefault(x => ReferenceEquals(x.Store, store))?.Replacement;
                if (replacement == null)
                {
                    Stores.Deregister(store);
                }

                foreach (var (endpointUri, listener) in tenantDatabaseListeners())
                {
                    if (await listener.StopListeningToDatabaseAsync(store, replacement))
                    {
                        result.StoppedListeners.Add($"{endpointUri} on {store.Name}");
                    }
                }

                await store.DisposeAsync();
                if (replacement == null) result.DeregisteredStores.Add(store.Uri);
            }
        }

        Logger.LogInformation(
            "Deregistered tenant {TenantId} from this node: stopped agents {Agents}, removed stores {Stores}, kept shared stores {Shared}, stopped listeners {Listeners}",
            tenantId, result.StoppedAgents, result.DeregisteredStores, result.SharedStoresKept, result.StoppedListeners);

        return result;
    }

    /// <summary>
    ///     GH-5840. Run on the leader when another node deregistered a tenant: deregister it here too (a no-op when
    ///     that already happened) and drop the removed agents from every later assignment evaluation, which stops
    ///     them wherever they still run.
    /// </summary>
    internal async Task DeregisterTenantAsLeaderAsync(string tenantId, Uri[] agentUris)
    {
        var local = await deregisterTenantLocallyAsync(tenantId);
        Stores.MarkAgentsRemoved(agentUris.Concat(local.DeregisteredStores));
    }

    private IEnumerable<(Uri EndpointUri, ITenantDatabaseListener Listener)> tenantDatabaseListeners()
    {
        foreach (var agent in Endpoints.ActiveListeners())
        {
            var listener = agent is ListeningAgent listening ? listening.Listener : null;
            if (listener is ParallelListener parallel)
            {
                foreach (var inner in parallel.InnerListeners.OfType<ITenantDatabaseListener>())
                {
                    yield return (agent.Uri, inner);
                }
            }
            else if (listener is ITenantDatabaseListener tenantListener)
            {
                yield return (agent.Uri, tenantListener);
            }
        }
    }

    /// <summary>
    ///     GH-5840. Envelopes still in each of a tenant's databases, for an operator tool to warn or refuse before it
    ///     deregisters the tenant. Read-only.
    /// </summary>
    internal async Task<IReadOnlyList<TenantPendingWork>> FindPendingWorkForTenantAsync(string tenantId)
    {
        var list = new List<TenantPendingWork>();
        if (!Stores.MultiTenanted.Any()) return list;

        foreach (var store in await Stores.FindForTenantAsync(tenantId))
        {
            var counts = await store.Admin.FetchCountsAsync();
            list.Add(new TenantPendingWork(store.Uri, counts.Incoming, counts.Outgoing, counts.Scheduled));
        }

        return list;
    }
}

/// <summary>
///     GH-5840. Sent to the leader by a node that deregistered a tenant. Idempotent: every node of the fleet sends it.
/// </summary>
internal record DeregisterTenantOnLeader(string TenantId, Uri[] AgentUris) : IAgentCommand
{
    public async Task<AgentCommands> ExecuteAsync(IWolverineRuntime runtime, CancellationToken cancellationToken)
    {
        if (runtime is WolverineRuntime wolverineRuntime)
        {
            await wolverineRuntime.DeregisterTenantAsLeaderAsync(TenantId, AgentUris);
        }

        return AgentCommands.Empty;
    }
}
