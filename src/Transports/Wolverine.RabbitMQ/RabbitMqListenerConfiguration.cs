using System;
using Wolverine.Configuration;
using Wolverine.ErrorHandling;
using Wolverine.RabbitMQ.Internal;
using Wolverine.Runtime.Interop.MassTransit;

namespace Wolverine.RabbitMQ
{
    public class RabbitMqListenerConfiguration : ListenerConfiguration<RabbitMqListenerConfiguration, RabbitMqEndpoint>
    {
        public RabbitMqListenerConfiguration(RabbitMqEndpoint endpoint) : base(endpoint)
        {
        }

        /// <summary>
        ///     To optimize the message listener throughput,
        ///     start up multiple listening endpoints. This is
        ///     most necessary when using inline processing
        /// </summary>
        /// <param name="count"></param>
        /// <returns></returns>
        public RabbitMqListenerConfiguration ListenerCount(int count)
        {
            if (count <= 0)
            {
                throw new ArgumentOutOfRangeException(nameof(count), "Must be greater than zero");
            }
            
            add(e => e.ListenerCount = count);

            return this;
        }

        /// <summary>
        /// Add circuit breaker exception handling to this listener
        /// </summary>
        /// <param name="configure"></param>
        /// <returns></returns>
        public RabbitMqListenerConfiguration CircuitBreaker(Action<CircuitBreakerOptions>? configure = null)
        {
            add(e =>
            {
                e.CircuitBreakerOptions = new CircuitBreakerOptions();
                configure?.Invoke(e.CircuitBreakerOptions);
            });

            return this;
        }

        /// <summary>
        ///     Assume that any unidentified, incoming message types is the
        ///     type "T". This is primarily for interoperability with non-Wolverine
        ///     applications
        /// </summary>
        /// <typeparam name="T"></typeparam>
        /// <returns></returns>
        public RabbitMqListenerConfiguration DefaultIncomingMessage<T>()
        {
            return DefaultIncomingMessage(typeof(T));
        }

        /// <summary>
        ///     Assume that any unidentified, incoming message types is the
        ///     type "T". This is primarily for interoperability with non-Wolverine
        ///     applications
        /// </summary>
        /// <typeparam name="T"></typeparam>
        /// <returns></returns>
        public RabbitMqListenerConfiguration DefaultIncomingMessage(Type messageType)
        {
            add(e => e.ReceivesMessage(messageType));
            return this;
        }

        /// <summary>
        /// Override the Rabbit MQ PreFetchCount value for just this endpoint for how many
        /// messages can be pre-fetched into memory before being handled
        /// </summary>
        /// <param name="count"></param>
        /// <returns></returns>
        public RabbitMqListenerConfiguration PreFetchCount(ushort count)
        {
            add(e => e.PreFetchCount = count);
            return this;
        }

        /// <summary>
        /// Override the Rabbit MQ PreFetchSize value for just this endpoint for the total size of the
        /// messages that can be pre-fetched into memory before being handled
        /// </summary>
        /// <param name="count"></param>
        /// <returns></returns>
        public RabbitMqListenerConfiguration PreFetchSize(uint size)
        {
            add(e => e.PreFetchSize = size);
            return this;
        }

        /// <summary>
        /// Add MassTransit interoperability to this Rabbit MQ listening endpoint
        /// </summary>
        /// <param name="configure">Optionally configure the JSON serialization on this endpoint</param>
        /// <returns></returns>
        public RabbitMqListenerConfiguration UseMassTransitInterop(Action<IMassTransitInterop>? configure = null)
        {
            add(e => e.UseMassTransitInterop(configure));
            return this;
        }

        /// <summary>
        /// Add NServiceBus interoperability to this Rabbit MQ listening endpoint
        /// </summary>
        /// <returns></returns>
        public RabbitMqListenerConfiguration UseNServiceBusInterop()
        {
            add(e => e.UseNServiceBusInterop());
            return this;
        }
    }
}
