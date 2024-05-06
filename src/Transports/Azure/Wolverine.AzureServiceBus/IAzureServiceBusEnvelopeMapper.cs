using Azure.Messaging.ServiceBus;
using Wolverine.Transports;

namespace Wolverine.AzureServiceBus;

public interface IAzureServiceBusEnvelopeMapper : IEnvelopeMapper<ServiceBusReceivedMessage, ServiceBusMessage>;