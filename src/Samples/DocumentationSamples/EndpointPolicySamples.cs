using Microsoft.Extensions.Hosting;
using Wolverine;
using Wolverine.Configuration;
using Wolverine.Runtime;

namespace DocumentationSamples;

#region sample_custom_endpoint_policy

// Force every application listening endpoint to process messages inline.
// Wolverine's own internal/system endpoints are left alone.
public class InlineListenersPolicy : IEndpointPolicy
{
    public void Apply(Endpoint endpoint, IWolverineRuntime runtime)
    {
        // Don't touch endpoints that Wolverine itself owns
        if (endpoint.Role == EndpointRole.System) return;

        if (endpoint.IsListener)
        {
            endpoint.Mode = EndpointMode.Inline;
        }
    }
}

#endregion

public static class EndpointPolicyRegistration
{
    public static async Task bootstrap()
    {
        #region sample_register_endpoint_policy

        using var host = await Host.CreateDefaultBuilder()
            .UseWolverine(opts =>
            {
                // Register a custom IEndpointPolicy type
                opts.Policies.Add<InlineListenersPolicy>();

                // Or register a policy inline with LambdaEndpointPolicy<T>.
                // The lambda only runs for endpoints assignable to T (here, every Endpoint)
                opts.Policies.Add(new LambdaEndpointPolicy<Endpoint>((endpoint, runtime) =>
                {
                    if (endpoint.Role == EndpointRole.System) return;
                    endpoint.Mode = EndpointMode.BufferedInMemory;
                }));
            }).StartAsync();

        #endregion
    }
}
