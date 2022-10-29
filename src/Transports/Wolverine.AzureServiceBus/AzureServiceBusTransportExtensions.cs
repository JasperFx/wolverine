using Azure.Messaging.ServiceBus;
using Baseline;
using Wolverine.AzureServiceBus.Internal;
using Wolverine.Configuration;

namespace Wolverine.AzureServiceBus;

public static class AzureServiceBusTransportExtensions
{
    /// <summary>
    ///     Quick access to the Rabbit MQ Transport within this application.
    ///     This is for advanced usage
    /// </summary>
    /// <param name="endpoints"></param>
    /// <returns></returns>
    internal static AzureServiceBusTransport AzureServiceBusTransport(this WolverineOptions endpoints)
    {
        var transports = endpoints.As<WolverineOptions>().Transports;

        return transports.GetOrCreate<AzureServiceBusTransport>();
    }

    public static AzureServiceBusConfiguration UseAzureServiceBus(this WolverineOptions endpoints,
        string connectionString, Action<ServiceBusClientOptions>? configure = null)
    {
        var transport = endpoints.AzureServiceBusTransport();
        transport.ConnectionString = connectionString ?? throw new ArgumentNullException(nameof(connectionString));
        configure?.Invoke(transport.ClientOptions);

        return new AzureServiceBusConfiguration(transport, endpoints);
    }
    
    /// <summary>
    ///     Listen for incoming messages at the designated Rabbit MQ queue by name
    /// </summary>
    /// <param name="endpoints"></param>
    /// <param name="queueName">The name of the Rabbit MQ queue</param>
    /// <param name="configure">
    ///     Optional configuration for this Rabbit Mq queue if being initialized by Wolverine
    ///     <returns></returns>
    public static AzureServiceBusListenerConfiguration ListenToAzureServiceBusQueue(this WolverineOptions endpoints, string queueName, Action<IAzureServiceBusListeningEndpoint>? configure = null )
    {
        var transport = endpoints.AzureServiceBusTransport();

        var corrected = transport.MaybeCorrectName(queueName);
        var endpoint = transport.Queues[corrected];
        endpoint.EndpointName = queueName;
        endpoint.IsListener = true;
        
        configure?.Invoke(endpoint);

        return new AzureServiceBusListenerConfiguration(endpoint);
    }

    public static AzureServiceBusSubscriberConfiguration ToAzureServiceBusQueue(this IPublishToExpression publishing, string queueName)
    {
        var transports = publishing.As<PublishingExpression>().Parent.Transports;
        var transport = transports.GetOrCreate<AzureServiceBusTransport>();
        
        var corrected = transport.MaybeCorrectName(queueName);

        var endpoint = transport.Queues[corrected];
        endpoint.EndpointName = queueName;
        
        // This is necessary unfortunately to hook up the subscription rules
        publishing.To(endpoint.Uri);

        return new AzureServiceBusSubscriberConfiguration(endpoint);
    }
}