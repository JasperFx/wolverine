using JasperFx.Core;
using Wolverine.Runtime;
using Wolverine.Runtime.Agents;

namespace Wolverine.RDBMS.MultiTenancy;

public partial class MultiTenantedMessageDatabase : IAgentFamily
{
    public string Scheme { get; } = DatabaseAgent.AgentScheme;
    public ValueTask<IReadOnlyList<Uri>> AllKnownAgentsAsync()
    {
        var uris = databases().Select(x => new Uri($"{Scheme}://{x.Name}")).ToList();
        return new ValueTask<IReadOnlyList<Uri>>(uris);
    }

    public ValueTask<IAgent> BuildAgentAsync(Uri uri, IWolverineRuntime wolverineRuntime)
    {
        var database = databases().FirstOrDefault(x => x.Name.EqualsIgnoreCase(uri.Host));
        if (database == null) throw new ArgumentOutOfRangeException(nameof(uri), "Unknown database " + uri.Host);

        return new ValueTask<IAgent>(new DatabaseAgent(database.Name, _runtime, database));
    }

    public ValueTask<IReadOnlyList<Uri>> SupportedAgentsAsync()
    {
        var uris = databases().Select(x => new Uri($"{Scheme}://{x.Name}")).ToList();
        return new ValueTask<IReadOnlyList<Uri>>(uris);
    }

    public ValueTask EvaluateAssignmentsAsync(AssignmentGrid assignments)
    {
        assignments.DistributeEvenly(Scheme);
        return new ValueTask();
    }
}