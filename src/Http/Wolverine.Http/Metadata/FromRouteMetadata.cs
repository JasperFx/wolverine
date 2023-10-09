using Microsoft.AspNetCore.Http.Metadata;

namespace Wolverine.Http.Metadata;

internal class FromRouteMetadata : IFromRouteMetadata
{
    public FromRouteMetadata(string? name)
    {
        Name = name;
    }

    public string? Name { get; }
}