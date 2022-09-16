using System.Collections.Generic;
using System.Linq;
using Oakton.Resources;

namespace Wolverine.Runtime;

public partial class WolverineRuntime : IStatefulResourceSource
{
    IReadOnlyList<IStatefulResource> IStatefulResourceSource.FindResources()
    {
        var list = new List<IStatefulResource>();
        list.AddRange(Options.OfType<IStatefulResource>());

        return list;
    }
}
