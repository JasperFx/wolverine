using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using RabbitMQ.Client;
using RabbitMQ.Client.Exceptions;
using Wolverine.Configuration;
using Wolverine.Runtime;
using Wolverine.Transports;

namespace Wolverine.RabbitMQ.Internal
{
    public class RabbitMqQueue : RabbitMqEndpoint
    {
        private readonly RabbitMqTransport _parent;

        internal RabbitMqQueue(string queueName, RabbitMqTransport parent, EndpointRole role = EndpointRole.Application) : base(new Uri($"{RabbitMqTransport.ProtocolName}://{RabbitMqEndpoint.QueueSegment}/{queueName}"), role, parent)
        {
            _parent = parent;
            QueueName = EndpointName = queueName;
            Mode = EndpointMode.Inline;
        }
        
        public string QueueName { get; }

        public bool AutoDelete { get; set; }

        public bool IsExclusive { get; set; }

        public bool IsDurable { get; set; } = true;

        public IDictionary<string, object> Arguments { get; } = new Dictionary<string, object>();
        internal bool HasDeclared { get; private set; }

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

        internal override void Initialize(IModel channel, ILogger logger)
        {
            // This is a reply uri owned by another node, so get out of here
            if (QueueName.StartsWith("wolverine.") && Role == EndpointRole.Application)
            {
                return;
            }
            
            if (_parent.AutoProvision || Role == EndpointRole.System)
            {
                Declare(channel, logger);
            }

            if (Role == EndpointRole.System && AutoDelete) return;

            if (_parent.AutoPurgeAllQueues || PurgeOnStartup)
            {
                Purge(channel);
            }
        }

        internal override string RoutingKey()
        {
            return QueueName;
        }

        internal void Declare(IModel channel, ILogger logger)
        {
            if (HasDeclared) return;

            try
            {
                channel.QueueDeclare(QueueName, IsDurable, IsExclusive, AutoDelete, Arguments);
                logger.LogInformation("Declared Rabbit MQ queue '{Name}' as IsDurable={IsDurable}, IsExclusive={IsExclusive}, AutoDelete={AutoDelete}", EndpointName, IsDurable, IsExclusive, AutoDelete);
            }
            catch (OperationInterruptedException e)
            {
                if (e.Message.Contains("inequivalent arg"))
                {
                    logger.LogDebug("Queue {Queue} exists with different configuration", QueueName);
                    return;
                }

                throw;
            }

            HasDeclared = true;
        }

        internal void Teardown(IModel channel)
        {
            channel.QueueDeleteNoWait(EndpointName);
        }

        internal void Purge(IModel channel)
        {
            try
            {
                channel.QueuePurge(QueueName);
            }
            catch (Exception e)
            {
                Console.WriteLine("Unable to purge queue " + QueueName);
                Console.WriteLine(e);
            }
        }

        internal void Initialize(IModel channel, ILogger logger, bool autoPurgeAllQueues)
        {
            Declare(channel, logger);
            if (!IsDurable || IsExclusive || AutoDelete) return;
            if (PurgeOnStartup || autoPurgeAllQueues)
            {
                channel.QueuePurge(QueueName);
            }
        }
        
        public override IDictionary<string, object> DescribeProperties()
        {
            var dict = base.DescribeProperties();

            dict.Add(nameof(QueueName), QueueName);

            if (ListenerCount > 0 && IsListener)
            {
                dict.Add(nameof(ListenerCount), ListenerCount);
            }

            return dict;
        }
        
        /// <summary>
        /// Number of parallel listeners for this queue endpoint
        /// </summary>
        public int ListenerCount { get; set; }
        
        /// <summary>
        /// Limit on the combined size of pre-fetched messages. The default in Wolverine is 0, which
        /// denotes an unlimited size.
        /// </summary>
        public uint PreFetchSize { get; set; }

        private ushort? _preFetchCount;

        /// <summary>
        /// The number of unacknowledged messages that can be processed concurrently
        /// </summary>
        public ushort PreFetchCount
        {
            get
            {
                if (_preFetchCount.HasValue) return _preFetchCount.Value;

                switch (Mode)
                {
                    case EndpointMode.BufferedInMemory:
                    case EndpointMode.Durable:
                        return (ushort)(ExecutionOptions.MaxDegreeOfParallelism * 2);

                }

                return 100;
            }
            set => _preFetchCount = value;
        }

        public override ValueTask<IListener> BuildListenerAsync(IWolverineRuntime runtime, IReceiver receiver)
        {
            var listener = ListenerCount > 1
                ? (IListener)new ParallelRabbitMqListener(runtime, this, _parent, receiver)
                : new RabbitMqListener(runtime, this, _parent, receiver);
            
            return ValueTask.FromResult<IListener>(listener);
        }

    }
}
