using JasperFx.Core;
using JasperFx.Core.Reflection;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using RabbitMQ.Client;
using Wolverine.Configuration;
using Wolverine.RabbitMQ.Internal;
using Wolverine.Runtime.Routing;

namespace Wolverine.RabbitMQ;

public static class RabbitMqTransportExtensions
{
    /// <summary>
    ///     Quick access to the Rabbit MQ Transport within this application.
    ///     This is for advanced usage
    /// </summary>
    /// <param name="endpoints"></param>
    /// <returns></returns>
    internal static RabbitMqTransport RabbitMqTransport(this WolverineOptions endpoints)
    {
        var transports = endpoints.As<WolverineOptions>().Transports;

        return transports.GetOrCreate<RabbitMqTransport>();
    }

    /// <summary>
    /// Publish messages that are of type T or could be cast to type T to a Rabbit MQ
    /// topic exchange using the supplied function to determine the topic for the message
    /// </summary>
    /// <param name="exchangeName"></param>
    /// <param name="topicSource"></param>
    /// <typeparam name="T"></typeparam>
    /// <returns></returns>
    public static RabbitMqExchangeConfiguration PublishMessagesToRabbitMqExchange<T>(this WolverineOptions options, string exchangeName,
        Func<T, string> topicSource)
    {
        var transport = options.RabbitMqTransport();
        var exchange = transport.Exchanges[exchangeName];
        exchange.ExchangeType = ExchangeType.Topic;
        exchange.RoutingType = RoutingMode.ByTopic;

        var routing = new TopicRouting<T>(topicSource, exchange);
        options.PublishWithMessageRoutingSource(routing);

        return new RabbitMqExchangeConfiguration(exchange);
    }

    /// <summary>
    ///     Configure connection and authentication information about the Rabbit MQ usage
    ///     within this Wolverine application
    /// </summary>
    /// <param name="options"></param>
    /// <param name="configure"></param>
    public static RabbitMqTransportExpression UseRabbitMq(this WolverineOptions options,
        Action<ConnectionFactory> configure)
    {
        var transport = options.RabbitMqTransport();
        
        transport.ConfigureFactory(configure);

        return new RabbitMqTransportExpression(transport, options);
    }

    /// <summary>
    ///     Connect to Rabbit MQ on the local machine with all the default
    ///     Rabbit MQ client options
    /// </summary>
    /// <param name="options"></param>
    /// <param name="rabbitMqUri">
    ///     Rabbit MQ Uri that designates the connection information. See
    ///     https://www.rabbitmq.com/uri-spec.html
    /// </param>
    public static RabbitMqTransportExpression UseRabbitMq(this WolverineOptions options, Uri rabbitMqUri)
    {
        return options.UseRabbitMq(factory => factory.Uri = rabbitMqUri);
    }

    /// <summary>
    /// Add a default Rabbit MQ broker connection to this application. Wolverine will first look in the IoC container
    /// for a registered IConnectionFactory (this would apply if using Aspire), but fallback to trying to connect
    /// to a Rabbit MQ broker locally with the default port
    /// </summary>
    /// <param name="options"></param>
    public static RabbitMqTransportExpression UseRabbitMq(this WolverineOptions options)
    {
        var transport = options.RabbitMqTransport();
        return new RabbitMqTransportExpression(transport, options);
    }

    /// <summary>
    /// Direct Wolverine to connect to Rabbit MQ using the named connection string from IConfiguration.
    /// This option is useful for Aspire integration between Wolverine and Aspire
    /// </summary>
    /// <param name="options"></param>
    /// <param name="connectionStringName">Key of the expected connection string from IConfiguration</param>
    /// <param name="configure">Optional extra configuration of the ConnectionFactory</param>
    /// <returns></returns>
    public static RabbitMqTransportExpression UseRabbitMqUsingNamedConnection(this WolverineOptions options,
        string connectionStringName, Action<ConnectionFactory>? configure = null)
    {
        options.Services.AddSingleton<IConnectionFactory>(s =>
        {
            var configuration = s.GetRequiredService<IConfiguration>();
            var connectionString = configuration.GetConnectionString(connectionStringName);
            if (connectionString.IsEmpty())
                throw new InvalidOperationException(
                    $"The connection string named '{connectionStringName}' is missing in configuration");


            var factory = new ConnectionFactory();
            ConnectionStringParser.Apply(connectionString, factory);

            configure?.Invoke(factory);

            return factory;
        });
        
        var transport = options.RabbitMqTransport();
        return new RabbitMqTransportExpression(transport, options);
    }

    /// <summary>
    ///     Connect to Rabbit MQ using the values from the connection string. This format is
    /// purposely designed to be compatible with the syntax from https://docs.particular.net/transports/rabbitmq/connection-settings
    /// </summary>
    /// <param name="options"></param>
    public static RabbitMqTransportExpression UseRabbitMq(this WolverineOptions options, string rabbitMqConnectionString)
    {
        return options.UseRabbitMq(factory =>
        {
            ConnectionStringParser.Apply(rabbitMqConnectionString, factory);
        });
    }

    /// <summary>
    ///     Listen for incoming messages at the designated Rabbit MQ queue by name
    /// </summary>
    /// <param name="endpoints"></param>
    /// <param name="queueName">The name of the Rabbit MQ queue</param>
    /// <param name="configure">
    ///     Optional configuration for this Rabbit Mq queue if being initialized by Wolverine
    ///     <returns></returns>
    public static RabbitMqListenerConfiguration ListenToRabbitQueue(this WolverineOptions endpoints, string queueName,
        Action<IRabbitMqQueue>? configure = null)
    {
        var transport = endpoints.RabbitMqTransport();
        var corrected = transport.MaybeCorrectName(queueName);
        var queue = transport.Queues[corrected];
        queue.EndpointName = queueName;
        queue.IsListener = true;

        configure?.Invoke(queue);

        return new RabbitMqListenerConfiguration(queue, transport);
    }

    /// <summary>
    ///     Publish matching messages to Rabbit MQ using the named routing key or queue name and
    ///     optionally an exchange
    /// </summary>
    /// <param name="publishing"></param>
    /// <param name="topicName">
    ///     A static topic name if the exchange is topic-based
    /// </param>
    /// <param name="exchangeName">Exchange name</param>
    /// <returns></returns>
    public static RabbitMqSubscriberConfiguration ToRabbitTopic(this IPublishToExpression publishing,
        string topicName, string exchangeName)
    {
        var transports = publishing.As<PublishingExpression>().Parent.Transports;
        var transport = transports.GetOrCreate<RabbitMqTransport>();
        var corrected = transport.MaybeCorrectName(exchangeName);
        var exchange = transport.EndpointForExchange(corrected);
        exchange.EndpointName = exchangeName;
        exchange.ExchangeType = ExchangeType.Topic;

        var topicEndpoint = exchange.Topics[topicName];

        publishing.As<PublishingExpression>().AddSubscriber(topicEndpoint);

        return new RabbitMqSubscriberConfiguration(exchange);
    }

    /// <summary>
    ///     Publish matching messages straight to a Rabbit MQ queue using the named routing key or queue name and
    ///     optionally an exchange
    /// </summary>
    /// <param name="publishing"></param>
    /// <param name="routingKeyOrQueue">
    ///     This is used as the routing key when publishing. Can be either a binding key or a queue
    ///     name or a static topic name if the exchange is topic-based
    /// </param>
    /// <returns></returns>
    public static RabbitMqSubscriberConfiguration ToRabbitQueue(this IPublishToExpression publishing,
        string queueName, Action<IRabbitMqQueue>? configure = null)
    {
        var transports = publishing.As<PublishingExpression>().Parent.Transports;
        var transport = transports.GetOrCreate<RabbitMqTransport>();

        var corrected = transport.MaybeCorrectName(queueName);
        var queue = transport.Queues[corrected];
        queue.EndpointName = queueName;

        configure?.Invoke(queue);

        // This is necessary unfortunately to hook up the subscription rules
        publishing.To(queue.Uri);

        return new RabbitMqSubscriberConfiguration(queue);
    }

    /// <summary>
    ///     Publish matching messages to Rabbit MQ to the designated exchange. This is
    ///     appropriate for "fanout" exchanges where Rabbit MQ ignores the routing key
    /// </summary>
    /// <param name="publishing"></param>
    /// <param name="exchangeName">The Rabbit MQ exchange name</param>
    /// <param name="configure">Optional configuration of this exchange if Wolverine is doing the initialization in Rabbit MQ</param>
    /// <returns></returns>
    public static RabbitMqSubscriberConfiguration ToRabbitExchange(this IPublishToExpression publishing,
        string exchangeName, Action<IRabbitMqBindableExchange>? configure = null)
    {
        var transports = publishing.As<PublishingExpression>().Parent.Transports;
        var transport = transports.GetOrCreate<RabbitMqTransport>();

        var corrected = transport.MaybeCorrectName(exchangeName);
        var exchange = transport.Exchanges[corrected];
        exchange.EndpointName = exchangeName;

        var config = new RabbitMqExchangeConfigurationExpression(corrected, transport);
        configure?.Invoke(config);

        var endpoint = transport.EndpointForExchange(exchangeName);

        // This is necessary unfortunately to hook up the subscription rules
        publishing.To(endpoint.Uri);

        return new RabbitMqSubscriberConfiguration(endpoint);
    }

    /// <summary>
    ///     Publish matching messages to Rabbit MQ to the designated exchange. This is
    ///     appropriate for "direct" exchanges where Rabbit MQ needs the routing key
    /// </summary>
    /// <param name="publishing"></param>
    /// <param name="exchangeName"></param>
    /// <param name="routingKey"></param>
    /// <param name="configure"></param>
    /// <returns></returns>
    public static RabbitMqSubscriberConfiguration ToRabbitRoutingKey(this IPublishToExpression publishing, string exchangeName, string routingKey,
        Action<IRabbitMqExchange>? configure = null)
    {
        var transports = publishing.As<PublishingExpression>().Parent.Transports;
        var transport = transports.GetOrCreate<RabbitMqTransport>();

        var corrected = transport.MaybeCorrectName(exchangeName);
        var exchange = transport.Exchanges[corrected];
        configure?.Invoke(exchange);

        var endpoint = exchange.Routings[routingKey];

        // This is necessary unfortunately to hook up the subscription rules
        publishing.To(endpoint.Uri);

        return new RabbitMqSubscriberConfiguration(endpoint);
    }

    /// <summary>
    ///     Publish matching messages to Rabbit MQ to the designated topic exchange, with
    ///     the topic name derived from the message type name or an explicit override
    /// </summary>
    /// <param name="publishing"></param>
    /// <param name="exchangeName">The Rabbit MQ exchange name</param>
    /// <param name="configure">Optional configuration of this exchange if Wolverine is doing the initialization in Rabbit MQ</param>
    /// <returns></returns>
    public static RabbitMqSubscriberConfiguration ToRabbitTopics(this IPublishToExpression publishing,
        string exchangeName, Action<IRabbitMqBindableExchange>? configure = null)
    {
        var transports = publishing.As<PublishingExpression>().Parent.Transports;
        var transport = transports.GetOrCreate<RabbitMqTransport>();

        var exchange = transport.Exchanges[exchangeName];
        var config = new RabbitMqExchangeConfigurationExpression(exchangeName, transport);
        configure?.Invoke(config);
        exchange.ExchangeType = ExchangeType.Topic;

        var endpoint = transport.EndpointForExchange(exchangeName);
        endpoint.RoutingType = RoutingMode.ByTopic;

        // This is necessary unfortunately to hook up the subscription rules
        publishing.To(endpoint.Uri);

        return new RabbitMqSubscriberConfiguration(endpoint);
    }

    /// <summary>
    /// Access the Rabbit Mq configuration for this Wolverine application
    /// in order to add or modify the application. This was meant for Wolverine extensions
    /// </summary>
    /// <param name="options"></param>
    /// <returns></returns>
    public static RabbitMqTransportExpression ConfigureRabbitMq(this WolverineOptions options)
    {
        var transport = options.RabbitMqTransport();
        var expression = new RabbitMqTransportExpression(transport, options);
        return expression;
    }
}