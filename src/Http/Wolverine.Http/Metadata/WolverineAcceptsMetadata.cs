using Microsoft.AspNetCore.Http.Metadata;

namespace Wolverine.Http.Metadata;

internal class WolverineAcceptsMetadata : IAcceptsMetadata
{
    public WolverineAcceptsMetadata(HttpChain chain)
    {
        ContentTypes = new string[] { "application/json" };
        RequestType = chain.RequestType;
        IsOptional = false;
    }

    public IReadOnlyList<string> ContentTypes { get; }
    public Type? RequestType { get; }
    public bool IsOptional { get; }
}