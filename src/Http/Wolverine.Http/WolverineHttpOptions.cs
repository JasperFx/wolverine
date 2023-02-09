using System.Text.Json;
using Lamar;
using Wolverine.Configuration;

namespace Wolverine.Http;

[Singleton]
public class WolverineHttpOptions
{
    internal JsonSerializerOptions JsonSerializerOptions { get; set; } = new();
    internal EndpointGraph? Endpoints { get; set; }

    public List<IEndpointPolicy> Policies { get; } = new List<IEndpointPolicy>();

    /// <summary>
    /// Add a new IEndpointPolicy for the Wolverine endpoints
    /// </summary>
    /// <typeparam name="T"></typeparam>
    public void AddPolicy<T>() where T : IEndpointPolicy, new()
    {
        Policies.Add(new T());
    }

    /// <summary>
    /// Apply user-defined customizations to how endpoints are handled
    /// by Wolverine
    /// </summary>
    /// <param name="configure"></param>
    public void ConfigureEndpoints(Action<EndpointChain> configure)
    {
        var policy = new LambdaEndpointPolicy((c, _, _) => configure(c));
        Policies.Add(policy);
    }
}

