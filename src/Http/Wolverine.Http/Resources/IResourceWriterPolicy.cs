namespace Wolverine.Http.Resources;

public interface IResourceWriterPolicy
{
    bool TryApply(EndpointChain chain);
}