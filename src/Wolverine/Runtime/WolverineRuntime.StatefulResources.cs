using System.Collections.Generic;
using Oakton.Resources;

namespace Wolverine.Runtime;

public partial class WolverineRuntime : IStatefulResourceSource
{
    IReadOnlyList<IStatefulResource> IStatefulResourceSource.FindResources()
    {
        var list = new List<IStatefulResource>();

        foreach (var transport in Options.Transports)
        {
            if (transport.TryBuildStatefulResource(this, out var resource))
            {
                list.Add(resource!);
            }
        }

        return list;
    }
}