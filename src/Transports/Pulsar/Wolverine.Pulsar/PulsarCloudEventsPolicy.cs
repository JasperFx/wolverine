using System.Text.Json;
using Wolverine.Configuration;
using Wolverine.Runtime;
using Wolverine.Runtime.Interop;

namespace Wolverine.Pulsar;

/// <summary>
/// Applies CloudEvents interop to all Pulsar endpoints.
/// This is useful when all Pulsar communication should use CloudEvents format.
/// </summary>
public class PulsarCloudEventsPolicy : IEndpointPolicy
{
    private readonly JsonSerializerOptions _jsonSerializerOptions;

    public PulsarCloudEventsPolicy(JsonSerializerOptions? jsonSerializerOptions = null)
    {
        _jsonSerializerOptions = jsonSerializerOptions ?? new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };
    }

    public void Apply(Endpoint endpoint, IWolverineRuntime runtime)
    {
        if (endpoint is PulsarEndpoint pulsarEndpoint)
        {
            pulsarEndpoint.DefaultSerializer = new CloudEventsMapper(runtime.Options.HandlerGraph, _jsonSerializerOptions);
        }
    }
}
