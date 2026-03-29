using Azure.Messaging.ServiceBus.Administration;
using Wolverine.AzureServiceBus.Internal;
using Wolverine.Configuration;
using Wolverine.Runtime.Interop.MassTransit;

namespace Wolverine.AzureServiceBus;

public class AzureServiceBusTopicSubscriberConfiguration : InteroperableSubscriberConfiguration<
    AzureServiceBusTopicSubscriberConfiguration,
    AzureServiceBusTopic, IAzureServiceBusEnvelopeMapper, AzureServiceBusEnvelopeMapper>
{
    public AzureServiceBusTopicSubscriberConfiguration(AzureServiceBusTopic endpoint) : base(endpoint)
    {
    }

    /// <summary>
    ///     Configure the underlying Azure Service Bus Topic. This is only applicable when
    ///     Wolverine is creating the Topics
    /// </summary>
    /// <param name="configure"></param>
    /// <returns></returns>
    public AzureServiceBusTopicSubscriberConfiguration ConfigureTopic(Action<CreateTopicOptions> configure)
    {
        add(e => configure(e.Options));
        return this;
    }

    /// <summary>
    /// Utilize custom envelope mapping for Amazon Service Bus interoperability with external non-Wolverine systems
    /// </summary>
    /// <param name="mapper"></param>
    /// <returns></returns>
    public AzureServiceBusTopicSubscriberConfiguration InteropWith(IAzureServiceBusEnvelopeMapper mapper)
    {
        add(e => e.EnvelopeMapper = mapper);
        return this;
    }

    /// <summary>
    /// Use envelope mapping that is interoperable with MassTransit
    /// </summary>
    /// <returns></returns>
    public AzureServiceBusTopicSubscriberConfiguration UseMassTransitInterop()
    {
        add(e => e.UseMassTransitInterop());
        return this;
    }

    /// <summary>
    /// Use envelope mapping that is interoperable with NServiceBus
    /// </summary>
    /// <returns></returns>
    public AzureServiceBusTopicSubscriberConfiguration UseNServiceBusInterop()
    {
        add(e => e.UseNServiceBusInterop());
        return this;
    }
}