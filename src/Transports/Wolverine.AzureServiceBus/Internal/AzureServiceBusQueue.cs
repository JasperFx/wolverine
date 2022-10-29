using Microsoft.Extensions.Logging;
using Wolverine.Configuration;
using Wolverine.Runtime;
using Wolverine.Transports;
using Wolverine.Transports.Sending;

namespace Wolverine.AzureServiceBus.Internal;

public class AzureServiceBusQueue : AzureServiceBusEndpoint, IBrokerQueue
{
    public AzureServiceBusQueue(AzureServiceBusTransport parent, string queueName, EndpointRole role) : base(parent, new Uri($"{AzureServiceBusTransport.ProtocolName}://queue/{queueName}"), role)
    {
    }

    public override ValueTask<IListener> BuildListenerAsync(IWolverineRuntime runtime, IReceiver receiver)
    {
        throw new NotImplementedException();
    }

    protected override ISender CreateSender(IWolverineRuntime runtime)
    {
        throw new NotImplementedException();
    }

    public async ValueTask PurgeAsync(ILogger logger)
    {
        throw new NotImplementedException();
    }

    public async ValueTask<Dictionary<string, string>> GetAttributesAsync()
    {
        throw new NotImplementedException();
    }
}