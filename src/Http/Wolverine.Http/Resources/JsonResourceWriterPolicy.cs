using Wolverine.Http.CodeGen;

namespace Wolverine.Http.Resources;

internal class JsonResourceWriterPolicy : IResourceWriterPolicy
{
    public bool TryApply(EndpointChain chain)
    {
        if (chain.HasResourceType())
        {
            chain.Postprocessors.Add(new WriteJsonFrame(chain.Method.ReturnVariable));
            return true;
        }

        return false;
    }
}

