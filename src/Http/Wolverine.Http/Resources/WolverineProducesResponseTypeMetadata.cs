using Microsoft.AspNetCore.Http.Metadata;

namespace Wolverine.Http.Resources;

public class WolverineProducesResponseTypeMetadata : IProducesResponseTypeMetadata
{
    public Type? Type { get; init; }
    public int StatusCode { get; init; }
    public IEnumerable<string> ContentTypes => new[] { "application/json" };
}