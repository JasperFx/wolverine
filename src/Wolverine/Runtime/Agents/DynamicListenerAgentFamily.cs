using Wolverine.Persistence.Durability;

namespace Wolverine.Runtime.Agents;

/// <summary>
/// Agent family backing the GH-2685 dynamic-listener registry. The set of agents
/// is not fixed at boot — every assignment cycle <see cref="AllKnownAgentsAsync"/>
/// queries <see cref="IMessageStore.Listeners"/> for the current registered
/// listener URIs and projects each one through
/// <see cref="DynamicListenerUriEncoding.ToAgentUri"/>. The cluster's
/// <see cref="NodeAgentController"/> then uses
/// <see cref="AssignmentGrid.DistributeEvenly"/> to balance them across the
/// running nodes — one node per listener URI, so registering an MQTT topic
/// activates exactly one consumer somewhere in the cluster regardless of how
/// many nodes are alive.
///
/// Registration is opt-in via <see cref="DurabilitySettings.EnableDynamicListeners"/>:
/// when the flag is off, <see cref="NodeAgentController"/> never instantiates
/// this family and the listener-registry table is never queried. Combined with
/// the storage-side gate from PR #2700, the entire feature is zero-cost when
/// not in use.
///
/// Re-evaluation cadence is the cluster's existing
/// <see cref="DurabilitySettings.CheckAssignmentPeriod"/> (default 30s). A newly
/// registered URI takes up to one polling interval to be picked up — that's
/// good enough for the IoT-device add/remove cadence in the original use case
/// without needing a separate change-stream / pub-sub channel.
/// </summary>
internal sealed class DynamicListenerAgentFamily : IAgentFamily
{
    private readonly IWolverineRuntime _runtime;

    public DynamicListenerAgentFamily(IWolverineRuntime runtime)
    {
        _runtime = runtime;
    }

    public string Scheme => DynamicListenerUriEncoding.SchemeName;

    public async ValueTask<IReadOnlyList<Uri>> AllKnownAgentsAsync()
    {
        var listenerUris = await _runtime.Storage.Listeners.AllListenersAsync(_runtime.Cancellation)
            .ConfigureAwait(false);

        if (listenerUris.Count == 0)
        {
            return Array.Empty<Uri>();
        }

        var agents = new List<Uri>(listenerUris.Count);
        foreach (var uri in listenerUris)
        {
            agents.Add(DynamicListenerUriEncoding.ToAgentUri(uri));
        }

        return agents;
    }

    public ValueTask<IAgent> BuildAgentAsync(Uri uri, IWolverineRuntime wolverineRuntime)
    {
        var listenerUri = DynamicListenerUriEncoding.ToListenerUri(uri);
        var agent = new DynamicListenerAgent(wolverineRuntime, listenerUri);
        return ValueTask.FromResult<IAgent>(agent);
    }

    public ValueTask<IReadOnlyList<Uri>> SupportedAgentsAsync()
    {
        // Every node that has the family registered is capable of running any of
        // the listeners — actual transport-level "can this node reach the broker"
        // failures surface at StartAsync time rather than at assignment time.
        return AllKnownAgentsAsync();
    }

    public ValueTask EvaluateAssignmentsAsync(AssignmentGrid assignments)
    {
        assignments.DistributeEvenly(Scheme);
        return new ValueTask();
    }
}
