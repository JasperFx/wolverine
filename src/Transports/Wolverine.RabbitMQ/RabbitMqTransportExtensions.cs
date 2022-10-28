using System;
using Baseline;
using RabbitMQ.Client;
using Wolverine.Configuration;
using Wolverine.RabbitMQ.Internal;

namespace Wolverine.RabbitMQ
{

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
        ///     Configure connection and authentication information about the Rabbit MQ usage
        ///     within this Wolverine application
        /// </summary>
        /// <param name="options"></param>
        /// <param name="configure"></param>
        public static RabbitMqTransportExpression UseRabbitMq(this WolverineOptions options,
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
        public static RabbitMqTransportExpression UseRabbitMq(this WolverineOptions options, Uri rabbitMqUri)
        {
            return options.UseRabbitMq(factory => factory.Uri = rabbitMqUri);
        }

        /// <summary>
        ///     Connect to Rabbit MQ on the local machine with all the default
        ///     Rabbit MQ client options
        /// </summary>
        /// <param name="options"></param>
        public static RabbitMqTransportExpression UseRabbitMq(this WolverineOptions options)
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
        /// <param name="topicName">
        ///  A static topic name if the exchange is topic-based
        /// </param>
        /// <param name="exchangeName">Exchange name</param>
        /// <returns></returns>
        public static RabbitMqSubscriberConfiguration ToRabbitTopic(this IPublishToExpression publishing,
            string topicName, string exchangeName)
        {
            var transports = publishing.As<PublishingExpression>().Parent.Transports;
            var transport = transports.GetOrCreate<RabbitMqTransport>();
            var exchange = transport.EndpointForExchange(exchangeName);
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
            string queueName, Action<RabbitMqQueue>? configure = null)
        {
            var transports = publishing.As<PublishingExpression>().Parent.Transports;
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
            var transports = publishing.As<PublishingExpression>().Parent.Transports;
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
            var transports = publishing.As<PublishingExpression>().Parent.Transports;
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
