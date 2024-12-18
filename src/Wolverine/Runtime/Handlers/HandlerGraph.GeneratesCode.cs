using JasperFx.CodeGeneration;

namespace Wolverine.Runtime.Handlers;

public partial class HandlerGraph
{
    string ICodeFileCollection.ChildNamespace => "WolverineHandlers";

    public GenerationRules Rules { get; internal set; } = null!;

    IReadOnlyList<ICodeFile> ICodeFileCollection.BuildFiles()
    {

        return explodeAllFiles().ToList();
    }

    private IEnumerable<ICodeFile> explodeAllFiles()
    {
        foreach (var chain in Chains)
        {
            if (chain.Handlers.Any())
            {
                yield return chain;
            }
            else
            {
                foreach (var handlerChain in chain.ByEndpoint)
                {
                    yield return handlerChain;
                }
            }
        }
    }
}