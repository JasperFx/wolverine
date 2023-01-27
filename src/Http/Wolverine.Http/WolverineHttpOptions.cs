using System.Text.Json;
using Lamar;

namespace Wolverine.Http;

[Singleton]
public class WolverineHttpOptions
{
    public JsonSerializerOptions JsonSerializerOptions { get; internal set; } = new();
    public EndpointGraph? Endpoints { get; internal set; }
}