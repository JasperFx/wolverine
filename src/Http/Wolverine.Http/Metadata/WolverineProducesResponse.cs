using Microsoft.AspNetCore.Http.Metadata;

namespace Wolverine.Http.Metadata;

internal class WolverineProducesResponse : IProducesResponseTypeMetadata
{
    public Type? Type { get; set; }
    public int StatusCode { get; set; }
    public IEnumerable<string> ContentTypes { get; set; } = Array.Empty<string>();
}