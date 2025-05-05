using Wolverine.Configuration;
using Wolverine.Runtime;

namespace Wolverine.Pulsar;

/// <summary>
/// Applies the specified requeue policy to all Pulsar endpoints.
/// </summary>
/// <param name="enableRequeue"></param>
public class PulsarEnableRequeuePolicy(PulsarRequeue enableRequeue) : IEndpointPolicy
{
    public void Apply(Endpoint endpoint, IWolverineRuntime runtime)
    {
        if (endpoint is PulsarEndpoint e)
        {
            e.EnableRequeue = enableRequeue == PulsarRequeue.Enabled;
        }
    }
}

public enum PulsarRequeue
{
    Enabled,
    Disabled,
}
