using Wolverine.Configuration;
using Wolverine.Runtime;

namespace Wolverine.Pulsar;

/// <summary>
/// Applies the specified unsubscribe on close policy to all Pulsar endpoints.
/// </summary>
/// <param name="unsubscribeOnClose"></param>
public class PulsarUnsubscribeOnClosePolicy(PulsarUnsubscribeOnClose unsubscribeOnClose) : IEndpointPolicy
{
    public void Apply(Endpoint endpoint, IWolverineRuntime runtime)
    {
        if (endpoint is PulsarEndpoint e)
        {
            e.UnsubscribeOnClose = unsubscribeOnClose == PulsarUnsubscribeOnClose.Enabled;
        }
    }
}

public enum PulsarUnsubscribeOnClose
{
    Enabled,
    Disabled,
}
