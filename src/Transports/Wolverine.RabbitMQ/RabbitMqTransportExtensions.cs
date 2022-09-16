using System;
using Baseline;
using RabbitMQ.Client;
using Wolverine.Configuration;
using Wolverine.RabbitMQ.Internal;

namespace Wolverine.RabbitMQ
{
    public interface IRabbitMqTransportExpression
    {
        /// <summary>
        /// Opt into using conventional Rabbit MQ routing
        /// </summary>
        /// <param name="configure"></param>
        /// <returns></returns>
        IRabbitMqTransportExpression UseConventionalRouting(Action<RabbitMqMessageRoutingConvention>? configure = null);

        // TODO -- both options with environment = Development

        /// <summary>
        /// All Rabbit MQ exchanges, queues, and bindings should be declared at runtime by Wolverine.
        /// </summary>
        /// <returns></returns>
        IRabbitMqTransportExpression AutoProvision();

        /// <summary>
        /// All queues should be purged of existing messages on first usage
        /// </summary>
        /// <returns></returns>
        IRabbitMqTransportExpression AutoPurgeOnStartup();

        /// <summary>
        ///     Declare a binding from a Rabbit Mq exchange to a Rabbit MQ queue
        /// </summary>
        /// <param name="exchangeName"></param>
        /// <param name="configure">Optional configuration of the Rabbit MQ exchange</param>
        /// <returns></returns>
        IBindingExpression BindExchange(string exchangeName, Action<RabbitMqExchange>? configure = null);

        /// <summary>
        ///     Declare a binding from a Rabbit Mq exchange to a Rabbit MQ queue
        /// </summary>
        /// <param name="exchangeName"></param>
        /// <returns></returns>
        IBindingExpression BindExchange(string exchangeName, ExchangeType exchangeType);


        /// <summary>
        ///     Declare that a queue should be created with the supplied name and optional configuration
        /// </summary>
        /// <param name="queueName"></param>
        /// <param name="configure"></param>
        IRabbitMqTransportExpression DeclareQueue(string queueName, Action<RabbitMqQueue>? configure = null);

        /// <summary>
        ///     Declare a new exchange. The default exchange type is "fan out"
        /// </summary>
        /// <param name="exchangeName"></param>
        /// <param name="configure"></param>
        IRabbitMqTransportExpression DeclareExchange(string exchangeName, Action<RabbitMqExchange>? configure = null);

        /// <summary>
        ///     Declare a new exchange with the specified exchange type
        /// </summary>
        /// <param name="exchangeName"></param>
        /// <param name="configure"></param>
        IRabbitMqTransportExpression DeclareExchange(string exchangeName, ExchangeType exchangeType,
            bool isDurable = true, bool autoDelete = false);
    }

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
            var transports = endpoints.As<WolverineOptions>();

            return transports.GetOrCreate<RabbitMqTransport>();
        }

        /// <summary>
        ///     Configure connection and authentication information about the Rabbit MQ usage
        ///     within this Wolverine application
        /// </summary>
        /// <param name="options"></param>
        /// <param name="configure"></param>
        public static IRabbitMqTransportExpression UseRabbitMq(this WolverineOptions options,
            Action<ConnectionFactory> configure)
        {
            var transport = options.RabbitMqTransport();
            configure(transport.ConnectionFactory);

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
        public static IRabbitMqTransportExpression UseRabbitMq(this WolverineOptions options, Uri rabbitMqUri)
        {
            return options.UseRabbitMq(factory => factory.Uri = rabbitMqUri);
        }

        /// <summary>
        ///     Connect to Rabbit MQ on the local machine with all the default
        ///     Rabbit MQ client options
        /// </summary>
        /// <param name="options"></param>
        public static IRabbitMqTransportExpression UseRabbitMq(this WolverineOptions options)
        {
            return options.UseRabbitMq(_ => { });
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
            Action<RabbitMqQueue>? configure = null)
        {
            var transport = endpoints.RabbitMqTransport();
            var queue = transport.Queues[queueName];
            configure?.Invoke(queue);

            var endpoint = transport.EndpointForQueue(queueName);
            endpoint.IsListener = true;

            return new RabbitMqListenerConfiguration(endpoint);
        }

        /// <summary>
        ///     Publish matching messages to Rabbit MQ using the named routing key or queue name and
        ///     optionally an exchange
        /// </summary>
        /// <param name="publishing"></param>
        /// <param name="routingKeyOrQueueName">
        ///     This is used as the routing key when publishing. Can be either a binding key or a
        ///     queue name or a static topic name if the exchange is topic-based
        /// </param>
        /// <param name="exchangeName">Optional, you only need to supply this if you are using a non-default exchange</param>
        /// <returns></returns>
        public static RabbitMqSubscriberConfiguration ToRabbit(this IPublishToExpression publishing,
            string routingKeyOrQueueName, string exchangeName = "")
        {
            var transports = publishing.As<PublishingExpression>().Parent;
            var transport = transports.GetOrCreate<RabbitMqTransport>();
            var endpoint = transport.EndpointFor(routingKeyOrQueueName, exchangeName);

            // This is necessary unfortunately to hook up the subscription rules
            publishing.To(endpoint.Uri);

            return new RabbitMqSubscriberConfiguration(endpoint);
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
            string queueName, Action<RabbitMqQueue>? configure = null)
        {
            var transports = publishing.As<PublishingExpression>().Parent;
            var transport = transports.GetOrCreate<RabbitMqTransport>();

            var queue = transport.Queues[queueName];
            configure?.Invoke(queue);

            var endpoint = transport.EndpointForQueue(queueName);

            // This is necessary unfortunately to hook up the subscription rules
            publishing.To(endpoint.Uri);

            return new RabbitMqSubscriberConfiguration(endpoint);
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
            string exchangeName, Action<RabbitMqExchange>? configure = null)
        {
            var transports = publishing.As<PublishingExpression>().Parent;
            var transport = transports.GetOrCreate<RabbitMqTransport>();

            var exchange = transport.Exchanges[exchangeName];
            configure?.Invoke(exchange);

            var endpoint = transport.EndpointForExchange(exchangeName);

            // This is necessary unfortunately to hook up the subscription rules
            publishing.To(endpoint.Uri);

            return new RabbitMqSubscriberConfiguration(endpoint);
        }


        /// <summary>
        /// Publish matching messages to Rabbit MQ to the designated topic exchange, with
        /// the topic name derived from the message type name or an explicit override
        /// </summary>
        /// <param name="publishing"></param>
        /// <param name="exchangeName">The Rabbit MQ exchange name</param>
        /// <param name="configure">Optional configuration of this exchange if Wolverine is doing the initialization in Rabbit MQ</param>
        /// <returns></returns>
        public static RabbitMqSubscriberConfiguration ToRabbitTopics(this IPublishToExpression publishing,
            string exchangeName, Action<RabbitMqExchange>? configure = null)
        {
            var transports = publishing.As<PublishingExpression>().Parent;
            var transport = transports.GetOrCreate<RabbitMqTransport>();

            var exchange = transport.Exchanges[exchangeName];
            configure?.Invoke(exchange);
            exchange.ExchangeType = ExchangeType.Topic;

            var endpoint = transport.EndpointForExchange(exchangeName);
            endpoint.RoutingType = RoutingMode.ByTopic;

            // This is necessary unfortunately to hook up the subscription rules
            publishing.To(endpoint.Uri);

            return new RabbitMqSubscriberConfiguration(endpoint);
        }




    }
}
