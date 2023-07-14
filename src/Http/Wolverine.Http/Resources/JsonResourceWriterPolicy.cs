using Wolverine.Http.CodeGen;

namespace Wolverine.Http.Resources;

internal class JsonResourceWriterPolicy : IResourceWriterPolicy
{
    public bool TryApply(HttpChain chain)
    {
        if (chain.HasResourceType())
        {
            var resourceVariable = chain.Method.Creates.First();
            resourceVariable.OverrideName(resourceVariable.Usage + "_response");
            
            chain.Postprocessors.Add(new WriteJsonFrame(resourceVariable));
            return true;
        }

        return false;
    }
}