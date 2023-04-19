using Microsoft.AspNetCore.Http.Metadata;

namespace Wolverine.Http.Resources;

internal class ProducesResponseTypeMetadata : IProducesResponseTypeMetadata
{
    public Type? Type { get; init; }
    public int StatusCode { get; init; }
    public IEnumerable<string> ContentTypes => new string[] { "application/json" };
}