using System;
using System.Collections.Generic;
using Microsoft.Extensions.Logging;
using RabbitMQ.Client;

namespace Wolverine.RabbitMQ.Internal
{
    public class RabbitMqQueue
    {
        public RabbitMqQueue(string name)
        {
            Name = name;
        }

        public string Name { get; }

        public bool AutoDelete { get; set; } = false;

        public bool IsExclusive { get; set; } = false;

        public bool IsDurable { get; set; } = true;

        public IDictionary<string, object> Arguments { get; } = new Dictionary<string, object>();
        public bool HasDeclared { get; private set; }

        /// <summary>
        /// Create a "time to live" limit for messages in this queue. Sets the Rabbit MQ x-message-ttl argument on a queue
        /// </summary>
        /// <param name="limit"></param>
        public void TimeToLive(TimeSpan limit)
        {
            Arguments["x-message-ttl"] = Convert.ToInt32(limit.TotalMilliseconds);
        }

        /// <summary>
        /// Declare that Wolverine should purge the existing queue
        /// of all existing messages on startup
        /// </summary>
        public bool PurgeOnStartup { get; set; }

        internal void Declare(IModel channel, ILogger logger)
        {
            if (HasDeclared) return;

            channel.QueueDeclare(Name, IsDurable, IsExclusive, AutoDelete, Arguments);
            logger.LogInformation("Declared Rabbit MQ queue '{Name}' as IsDurable={IsDurable}, IsExclusive={IsExclusive}, AutoDelete={AutoDelete}", Name, IsDurable, IsExclusive, AutoDelete);

            HasDeclared = true;
        }

        public void Teardown(IModel channel)
        {
            channel.QueueDeleteNoWait(Name);
        }

        public void Purge(IModel channel)
        {
            try
            {
                channel.QueuePurge(Name);
            }
            catch (Exception e)
            {
                Console.WriteLine("Unable to purge queue " + Name);
                Console.WriteLine(e);
            }
        }
    }
}
