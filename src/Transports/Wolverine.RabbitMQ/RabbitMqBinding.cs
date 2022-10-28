using System;
using System.Collections.Generic;
using Microsoft.Extensions.Logging;
using RabbitMQ.Client;
using Wolverine.RabbitMQ.Internal;

namespace Wolverine.RabbitMQ
{
    public class RabbitMqBinding
    {
        private readonly RabbitMqQueue _queue;

        public RabbitMqBinding(string exchangeName, RabbitMqQueue queue, string? bindingKey = null)
        {
            ExchangeName = exchangeName ?? throw new ArgumentNullException(nameof(exchangeName));
            _queue = queue ?? throw new ArgumentNullException(nameof(queue));
            BindingKey = bindingKey ?? $"{ExchangeName}_{_queue.EndpointName}";
        }

        public string BindingKey { get; }
        public IRabbitMqQueue Queue => _queue;
        public string ExchangeName { get; }

        public IDictionary<string, object> Arguments { get; } = new Dictionary<string, object>();
        public bool HasDeclared { get; private set; }

        internal void Declare(IModel channel, ILogger logger)
        {
            if (HasDeclared) return;
            _queue.Declare(channel, logger);
            channel.QueueBind(_queue.EndpointName, ExchangeName, BindingKey, Arguments);
            logger.LogInformation("Declared a Rabbit Mq binding '{Key}' from exchange {Exchange} to {Queue}", BindingKey, ExchangeName, _queue.EndpointName);

            HasDeclared = true;
        }

        public void Teardown(IModel channel)
        {
            channel.QueueUnbind(_queue.EndpointName, ExchangeName, BindingKey, Arguments);
        }

        protected bool Equals(RabbitMqBinding other)
        {
            return BindingKey == other.BindingKey && Queue == other.Queue && ExchangeName == other.ExchangeName;
        }

        public override bool Equals(object? obj)
        {
            if (ReferenceEquals(null, obj))
            {
                return false;
            }

            if (ReferenceEquals(this, obj))
            {
                return true;
            }

            if (obj.GetType() != GetType())
            {
                return false;
            }

            return Equals((RabbitMqBinding)obj);
        }

        public override int GetHashCode()
        {
            return HashCode.Combine(BindingKey, Queue, ExchangeName);
        }

        public override string ToString()
        {
            return
                $"{nameof(BindingKey)}: {BindingKey}, {nameof(Queue)}: {Queue}, {nameof(ExchangeName)}: {ExchangeName}";
        }
    }
}
