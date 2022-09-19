using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using Baseline;
using RabbitMQ.Client;
using Wolverine.Configuration;
using Wolverine.Runtime;
using Wolverine.Runtime.Interop.MassTransit;
using Wolverine.Transports;
using Wolverine.Transports.Sending;
using Wolverine.Util;

namespace Wolverine.RabbitMQ.Internal
{
    public class RabbitMqEndpoint : TransportEndpoint<IBasicProperties>, IMassTransitInteropEndpoint
    {
        public const string QueueSegment = "queue";
        public const string ExchangeSegment = "exchange";
        public const string RoutingSegment = "routing";
        private readonly RabbitMqTransport _parent;

        public RabbitMqEndpoint(RabbitMqTransport parent)
        {
            MapProperty(x => x.CorrelationId!, (e, p) => e.CorrelationId = p.CorrelationId,
                (e, p) => p.CorrelationId = e.CorrelationId);
            MapProperty(x => x.ContentType!, (e, p) => e.ContentType = p.ContentType,
                (e, p) => p.ContentType = e.ContentType);

            MapProperty(x => x.DeliverBy!, (_, _) => {}, (e, props) =>
            {
                if (e.DeliverBy.HasValue)
                {
                    var ttl = Convert.ToInt32(e.DeliverBy.Value.Subtract(DateTimeOffset.Now).TotalMilliseconds);
                    props.Expiration = ttl.ToString();
                }
            });

            _parent = parent;

            Mode = EndpointMode.Inline;
        }

        public string ExchangeName { get; set; } = string.Empty;
        public string? RoutingKey { get; set; }

        public string? QueueName { get; set; }

        public int ListenerCount { get; set; }

        public override Uri Uri
        {
            get
            {
                var list = new List<string>();

                if (QueueName.IsNotEmpty())
                {
                    list.Add(QueueSegment);
                    list.Add(QueueName.ToLowerInvariant());
                }
                else
                {
                    if (ExchangeName.IsNotEmpty())
                    {
                        list.Add(ExchangeSegment);
                        list.Add(ExchangeName.ToLowerInvariant());
                    }

                    if (RoutingKey.IsNotEmpty())
                    {
                        list.Add(RoutingSegment);
                        list.Add(RoutingKey.ToLowerInvariant());
                    }
                }


                var uri = $"{RabbitMqTransport.ProtocolName}://{list.Join("/")}".ToUri();

                return uri;
            }
        }

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


        public override IDictionary<string, object> DescribeProperties()
        {
            var dict = base.DescribeProperties();

            if (ExchangeName.IsNotEmpty())
            {
                dict.Add(nameof(ExchangeName), ExchangeName);
            }

            if (RoutingKey.IsNotEmpty())
            {
                dict.Add(nameof(RoutingKey), RoutingKey);
            }

            if (QueueName.IsNotEmpty())
            {
                dict.Add(nameof(QueueName), QueueName);
            }

            if (ListenerCount > 0 && IsListener)
            {
                dict.Add(nameof(ListenerCount), ListenerCount);
            }

            return dict;
        }

        public override void Parse(Uri uri)
        {
            if (uri.Scheme != RabbitMqTransport.ProtocolName)
            {
                throw new ArgumentOutOfRangeException(nameof(uri),"This is not a rabbitmq Uri");
            }

            var raw = uri.Segments.Where(x => x != "/").Select(x => x.Trim('/'));
            var segments = new Queue<string>();
            segments.Enqueue(uri.Host);
            foreach (var segment in raw) segments.Enqueue(segment);


            while (segments.Any())
            {
                if (segments.Peek().EqualsIgnoreCase(ExchangeSegment))
                {
                    segments.Dequeue();
                    ExchangeName = segments.Dequeue();
                }
                else if (segments.Peek().EqualsIgnoreCase(QueueSegment))
                {
                    segments.Dequeue();
                    QueueName = segments.Dequeue();
                }
                else if (segments.Peek().EqualsIgnoreCase(RoutingSegment))
                {
                    segments.Dequeue();
                    RoutingKey = segments.Dequeue();
                }
                else if (segments.Peek().EqualsIgnoreCase(TransportConstants.Durable))
                {
                    segments.Dequeue();
                    Mode = EndpointMode.Durable;
                }
                else
                {
                    throw new InvalidOperationException($"The Uri '{uri}' is invalid for a Rabbit MQ endpoint");
                }
            }
        }

        public override IListener BuildListener(IWolverineRuntime runtime, IReceiver receiver)
        {
            return ListenerCount > 1
                ? new ParallelRabbitMqListener(runtime.Logger, this, _parent, receiver)
                : new RabbitMqListener(runtime.Logger, this, _parent, receiver);
        }

        protected override ISender CreateSender(IWolverineRuntime runtime)
        {
            return new RabbitMqSender(this, _parent, RoutingType, runtime.Logger);
        }

        protected override void writeOutgoingHeader(IBasicProperties outgoing, string key, string value)
        {
            outgoing.Headers[key] = value;
        }

        protected override bool tryReadIncomingHeader(IBasicProperties incoming, string key, out string? value)
        {
            if (incoming.Headers.TryGetValue(key, out var raw))
            {
                value = (raw is byte[] b ? Encoding.Default.GetString(b) : raw.ToString())!;
                return true;
            }

            value = null;
            return false;
        }

        protected override void writeIncomingHeaders(IBasicProperties incoming, Envelope envelope)
        {
            foreach (var pair in incoming.Headers)
                envelope.Headers[pair.Key] =
                    pair.Value is byte[] b ? Encoding.Default.GetString(b) : pair.Value?.ToString();
        }

        public Uri? MassTransitUri()
        {
            var segments = new List<string>();
            var virtualHost = _parent.ConnectionFactory.VirtualHost;
            if (virtualHost.IsNotEmpty() && virtualHost != "/")
            {
                segments.Add(virtualHost);
            }

            if (QueueName.IsNotEmpty())
            {
                segments.Add(QueueName);
            }
            else if (ExchangeName.IsNotEmpty())
            {
                segments.Add(ExchangeName);
            }
            else
            {
                return null;
            }

            return $"rabbitmq://{_parent.ConnectionFactory.HostName}/{segments.Join("/")}".ToUri();
        }

        public Uri? MassTransitReplyUri()
        {
            if (_parent.ReplyEndpoint() is RabbitMqEndpoint r)
            {
                return r.MassTransitUri();
            }

            return null;
        }

        public Uri? TranslateMassTransitToWolverineUri(Uri uri)
        {
            var lastSegment = uri.Segments.LastOrDefault();
            if (lastSegment.IsNotEmpty())
            {
                return $"rabbitmq://queue/{lastSegment}".ToUri();
            }

            return null;
        }

        public void UseMassTransitInterop(Action<IMassTransitInterop>? configure = null)
        {
            var serializer = new MassTransitJsonSerializer(this);
            configure?.Invoke(serializer);

            DefaultSerializer = serializer;

            var replyUri = new Lazy<string>(() => MassTransitReplyUri()?.ToString());

            MapOutgoingProperty(x => x.ReplyUri, (e, p) =>
            {
                p.Headers[MassTransitHeaders.ResponseAddress] = replyUri.Value;
            });

            MapPropertyToHeader(x => x.MessageType, MassTransitHeaders.MessageType);
        }
    }
}
