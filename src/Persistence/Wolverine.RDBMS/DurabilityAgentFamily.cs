using JasperFx.Core;
using Wolverine.Persistence;
using Wolverine.RDBMS.MultiTenancy;
using Wolverine.Runtime;
using Wolverine.Runtime.Agents;
using Wolverine.Transports;

namespace Wolverine.RDBMS;

public class DurabilityAgentFamily : IAgentFamily
{
    private readonly IWolverineRuntime _runtime;

    public DurabilityAgentFamily(IWolverineRuntime runtime)
    {
        _runtime = runtime;
    }

    public string Scheme => PersistenceConstants.AgentScheme;
    public async ValueTask<IReadOnlyList<Uri>> AllKnownAgentsAsync()
    {
        var list = new List<Uri>();

        if (_runtime.Storage is MultiTenantedMessageDatabase mt)
        {
            list.AddRange(await mt.AllKnownAgentsAsync());
        }
        else if (_runtime.Storage is IAgentFamily family)
        {
            list.AddRange(await family.AllKnownAgentsAsync());
        }

        foreach (var ancillaryStore in _runtime.AncillaryStores)
        {
            if (ancillaryStore is MultiTenantedMessageDatabase ancillaryMultiTenanted)
            {
                var raw = await ancillaryMultiTenanted.AllKnownAgentsAsync();
                list.AddRange(raw.Select(uri => DurabilityAgent.AddMarkerType(uri, ancillaryStore.MarkerType)));
            }
            else if (ancillaryStore is IAgentFamily family)
            {
                var raw = await family.AllKnownAgentsAsync();
                list.AddRange(raw.Select(uri => DurabilityAgent.AddMarkerType(uri, ancillaryStore.MarkerType)));
            }
        }

        return list;
    }

    public ValueTask<IAgent> BuildAgentAsync(Uri uri, IWolverineRuntime wolverineRuntime)
    {
        var segment = uri.Segments.LastOrDefault(x => x != "/");
        IAgentFamily family = segment.IsEmpty()
            ? _runtime.Storage as IAgentFamily
            : _runtime.AncillaryStores.FirstOrDefault(x => x.MarkerType.Name == segment) as IAgentFamily;

        if (family == null)
        {
            throw new InvalidAgentException($"Unknown durability agent '{uri}'");
        }

        return family.BuildAgentAsync(uri, wolverineRuntime);
    }

    public ValueTask<IReadOnlyList<Uri>> SupportedAgentsAsync()
    {
        return AllKnownAgentsAsync();
    }

    public ValueTask EvaluateAssignmentsAsync(AssignmentGrid assignments)
    {
        assignments.DistributeEvenly(Scheme);
        return ValueTask.CompletedTask;
    }
}