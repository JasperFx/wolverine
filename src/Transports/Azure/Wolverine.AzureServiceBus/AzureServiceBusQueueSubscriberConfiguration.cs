using Azure.Messaging.ServiceBus.Administration;
using Wolverine.AzureServiceBus.Internal;
using Wolverine.Configuration;

namespace Wolverine.AzureServiceBus;

public class AzureServiceBusQueueSubscriberConfiguration : SubscriberConfiguration<
    AzureServiceBusQueueSubscriberConfiguration,
    AzureServiceBusQueue>
{
    public AzureServiceBusQueueSubscriberConfiguration(AzureServiceBusQueue endpoint) : base(endpoint)
    {
    }

    /// <summary>
    ///     Configure the underlying Azure Service Bus queue. This is only applicable when
    ///     Wolverine is creating the queues
    /// </summary>
    /// <param name="configure"></param>
    /// <returns></returns>
    public AzureServiceBusQueueSubscriberConfiguration ConfigureQueue(Action<CreateQueueOptions> configure)
    {
        add(e => configure(e.Options));
        return this;
    }

    /// <summary>
    /// Force this queue to require session identifiers. Use this for FIFO semantics
    /// </summary>
    /// <param name="listenerCount">The maximum number of parallel sessions that can be processed at any one time</param>
    /// <returns></returns>
    public AzureServiceBusQueueSubscriberConfiguration RequireSessions(int? listenerCount = null)
    {
        add(e =>
        {
            e.Options.RequiresSession = true;
            if (listenerCount.HasValue)
            {
                e.ListenerCount = listenerCount.Value;
            }
        });

        return this;
    }

    /// <summary>
    /// Utilize custom envelope mapping for Amazon Service Bus interoperability with external non-Wolverine systems
    /// </summary>
    /// <param name="mapper"></param>
    /// <returns></returns>
    public AzureServiceBusQueueSubscriberConfiguration InteropWith(IAzureServiceBusEnvelopeMapper mapper)
    {
        add(e => e.Mapper = mapper);
        return this;
    }
}