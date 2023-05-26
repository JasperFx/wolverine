namespace Wolverine.Http.Resources;

public interface IResourceWriterPolicy
{
    bool TryApply(HttpChain chain);
}