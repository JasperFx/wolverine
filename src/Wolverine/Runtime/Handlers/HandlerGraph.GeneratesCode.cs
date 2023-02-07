using System.Collections.Generic;
using System.Linq;
using JasperFx.CodeGeneration;

namespace Wolverine.Runtime.Handlers;

public partial class HandlerGraph
{
    string ICodeFileCollection.ChildNamespace { get; } = "Wolverine.Handlers";

    public GenerationRules? Rules { get; internal set; }

    IReadOnlyList<ICodeFile> ICodeFileCollection.BuildFiles()
    {
        return Chains.ToList();
    }
}