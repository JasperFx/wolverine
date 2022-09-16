using System.Collections.Generic;
using System.Linq;
using LamarCodeGeneration;

namespace Wolverine.Runtime.Handlers;

public partial class HandlerGraph
{
    IReadOnlyList<ICodeFile> ICodeFileCollection.BuildFiles()
    {
        return Chains.ToList();
    }

    string ICodeFileCollection.ChildNamespace { get; } = "WolverineHandlers";

    public GenerationRules? Rules { get; internal set; }
}
