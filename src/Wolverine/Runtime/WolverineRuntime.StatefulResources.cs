using JasperFx.Resources;
using Wolverine.Persistence.Durability;

namespace Wolverine.Runtime;

public partial class WolverineRuntime : IStatefulResourceSource
{
    ValueTask<IReadOnlyList<IStatefulResource>> IStatefulResourceSource.FindResources()
    {
        var list = new List<IStatefulResource>();
        if (Options.ExternalTransportsAreStubbed) return new ValueTask<IReadOnlyList<IStatefulResource>>(list);

        foreach (var transport in Options.Transports)
        {
            if (transport.TryBuildStatefulResource(this, out var resource))
            {
                list.Add(resource!);
            }
        }

        foreach (var store in AncillaryStores)
        {
            list.Add(new MessageStoreResource(store));
        }

        return new ValueTask<IReadOnlyList<IStatefulResource>>(list);
    }
}