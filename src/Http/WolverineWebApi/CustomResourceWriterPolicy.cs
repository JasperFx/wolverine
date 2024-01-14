using Wolverine.Http;
using Wolverine.Http.Resources;

public class CustomResourceWriterPolicy : IResourceWriterPolicy
{
    public bool TryApply(HttpChain chain)
    {
        return false;
    }
}