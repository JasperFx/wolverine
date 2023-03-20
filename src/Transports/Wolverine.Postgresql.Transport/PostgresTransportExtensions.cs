using JasperFx.Core.Reflection;
using Wolverine.Configuration;
using Wolverine.Transports.Postgresql.Internal;

namespace Wolverine.Transports.Postgresql;

public static class PostgresTransportExtensions
{
    /// <summary>
    ///     Quick access to the Postgres Transport within this application.
    ///     This is for advanced usage
    /// </summary>
    /// <param name="endpoints"></param>
    /// <returns></returns>
    internal static PostgresTransport PostgresTransport(this WolverineOptions endpoints)
    {
        var transports = endpoints.As<WolverineOptions>().Transports;

        return transports.GetOrCreate<PostgresTransport>();
    }

    public static PostgresConfiguration UsePostgres(this WolverineOptions endpoints,
        string connectionString)
        // TODO cleanup
    // , Action<ServiceBusClientOptions>? configure = null
    {
        var transport = endpoints.PostgresTransport();
        transport.ConnectionString = connectionString ?? throw new ArgumentNullException(nameof(connectionString));
        //configure?.Invoke(transport.ClientOptions);

        return new PostgresConfiguration(transport, endpoints);
    }

    /// <summary>
    ///     Listen for incoming messages at the designated Rabbit MQ queue by name
    /// </summary>
    /// <param name="endpoints"></param>
    /// <param name="queueName">The name of the Rabbit MQ queue</param>
    /// <param name="configure">
    ///     Optional configuration for this Rabbit Mq queue if being initialized by Wolverine
    ///     <returns></returns>
    public static PostgresQueueListenerConfiguration ListenToPostgresQueue(
        this WolverineOptions endpoints, string queueName, Action<IPostgresListeningEndpoint>? configure = null)
    {
        var transport = endpoints.PostgresTransport();

        var corrected = transport.MaybeCorrectName(queueName);
        var endpoint = transport.Queues[corrected];
        endpoint.EndpointName = queueName;
        endpoint.IsListener = true;

        configure?.Invoke(endpoint);

        return new PostgresQueueListenerConfiguration(endpoint);
    }

    public static PostgresQueueSubscriberConfiguration ToPostgresQueue(
        this IPublishToExpression publishing, string queueName)
    {
        var transports = publishing.As<PublishingExpression>().Parent.Transports;
        var transport = transports.GetOrCreate<PostgresTransport>();

        var corrected = transport.MaybeCorrectName(queueName);

        var endpoint = transport.Queues[corrected];
        endpoint.EndpointName = queueName;

        // This is necessary unfortunately to hook up the subscription rules
        publishing.To(endpoint.Uri);

        return new PostgresQueueSubscriberConfiguration(endpoint);
    }
}