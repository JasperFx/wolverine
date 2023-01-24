namespace Wolverine.Http;

public interface IResourceWriterPolicy
{
    bool TryApply(EndpointChain chain);
}