using Wolverine.Http.CodeGen;

namespace Wolverine.Http.Resources;

internal class JsonResourceWriterPolicy : IResourceWriterPolicy
{
    public bool TryApply(HttpChain chain)
    {
        if (chain.HasResourceType())
        {
            chain.Postprocessors.Add(new WriteJsonFrame(chain.Method.Creates.First()));
            return true;
        }

        return false;
    }
}