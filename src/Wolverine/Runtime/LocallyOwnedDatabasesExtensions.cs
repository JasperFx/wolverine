using JasperFx.Descriptors;

namespace Wolverine.Runtime;

public static class LocallyOwnedDatabasesExtensions
{
    /// <summary>
    /// The databases whose event-subscription / projection agents run on this node right now: every
    /// database under <see cref="DurabilityMode.Solo" />, otherwise the databases of the agents currently
    /// running here (see <see cref="IAgentRuntime.AllLocallyOwnedDatabaseIds" />). Under
    /// <see cref="DurabilityMode.Serverless" />, <see cref="DurabilityMode.ServerlessWithOutbox" /> and
    /// <see cref="DurabilityMode.MediatorOnly" /> no agents run, so the answer is always
    /// <see cref="Wolverine.Runtime.Agents.LocallyOwnedDatabases.IsNone" />. Read fresh on every
    /// call, because assignments move as Wolverine rebalances.
    /// </summary>
    public static Wolverine.Runtime.Agents.LocallyOwnedDatabases LocallyOwnedDatabases(this IWolverineRuntime runtime)
    {
        ArgumentNullException.ThrowIfNull(runtime);

        if (runtime.Options.Durability.Mode == DurabilityMode.Solo)
        {
            return Wolverine.Runtime.Agents.LocallyOwnedDatabases.Every;
        }

        return new Wolverine.Runtime.Agents.LocallyOwnedDatabases(runtime.Agents.AllLocallyOwnedDatabaseIds().ToHashSet<DatabaseId>());
    }
}
