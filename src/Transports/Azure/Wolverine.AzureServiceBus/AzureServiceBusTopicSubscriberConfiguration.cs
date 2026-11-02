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
    ///     Mark this topic as owned by another system. Wolverine will not create the topic at startup or in
    ///     <c>resources setup</c>, or delete it in <c>resources teardown</c>, even when <c>AutoProvision()</c> is on.
    ///     <c>resources check</c> still checks that it exists. GH-5342.
    /// </summary>
    public AzureServiceBusTopicSubscriberConfiguration ExternallyOwned()
    {
        add(e => e.IsExternallyOwned = true);
        return this;
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