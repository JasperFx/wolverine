using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Baseline;
using Microsoft.Extensions.Logging;
using RabbitMQ.Client;
using Wolverine.Configuration;
using Wolverine.Runtime;
using Wolverine.Runtime.Interop.MassTransit;
using Wolverine.Transports;
using Wolverine.Transports.Sending;
using Wolverine.Util;

namespace Wolverine.RabbitMQ.Internal
{
    public abstract class RabbitMqEndpoint : Endpoint, IMassTransitInteropEndpoint
    {
        public const string QueueSegment = "queue";
        public const string ExchangeSegment = "exchange";
        private readonly RabbitMqTransport _parent;

        internal RabbitMqEndpoint(Uri uri, EndpointRole role, RabbitMqTransport parent) : base(uri, role)
        {
            _parent = parent;

            Mode = EndpointMode.Inline;
        }

        public string ExchangeName { get; protected set; } = string.Empty;

        internal abstract string RoutingKey();


        public override IDictionary<string, object> DescribeProperties()
        {
            var dict = base.DescribeProperties();

            if (ExchangeName.IsNotEmpty())
            {
                dict.Add(nameof(ExchangeName), ExchangeName);
            }

            return dict;
        }
        
        protected override ISender CreateSender(IWolverineRuntime runtime)
        {
            return new RabbitMqSender(this, _parent, RoutingType, runtime);
        }
        
        // TODO -- have this built in the constructors?
        public Uri? MassTransitUri()
        {
            var segments = new List<string>();
            var virtualHost = _parent.ConnectionFactory.VirtualHost;
            if (virtualHost.IsNotEmpty() && virtualHost != "/")
            {
                segments.Add(virtualHost);
            }

            var routingKey = RoutingKey();
            if (routingKey.IsNotEmpty())
            {
                segments.Add(routingKey);
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

        private Action<RabbitMqEnvelopeMapper> _customizeMapping = m => { };

        public void UseMassTransitInterop(Action<IMassTransitInterop>? configure = null)
        {
            var serializer = new MassTransitJsonSerializer(this);
            configure?.Invoke(serializer);

            DefaultSerializer = serializer;

            var replyUri = new Lazy<string>(() => MassTransitReplyUri()?.ToString() ?? string.Empty);

            _customizeMapping = m =>
            {
                m.MapOutgoingProperty(x => x.ReplyUri!, (e, p) =>
                {
                    p.Headers[MassTransitHeaders.ResponseAddress] = replyUri.Value;
                });

                m.MapPropertyToHeader(x => x.MessageType!, MassTransitHeaders.MessageType);
            };
        }

        public void UseNServiceBusInterop()
        {
            _customizeMapping = m =>
            {
                m.MapPropertyToHeader(x => x.ConversationId, "NServiceBus.ConversationId");
                m.MapPropertyToHeader(x => x.SentAt, "NServiceBus.TimeSent");

                var replyAddress = new Lazy<string>(() =>
                {
                    var replyEndpoint = (RabbitMqEndpoint)_parent.ReplyEndpoint()!;
                    return replyEndpoint.RoutingKey();
                });

                void WriteReplyToAddress(Envelope e, IBasicProperties props) => props.Headers["NServiceBus.ReplyToAddress"] = replyAddress.Value;

                void ReadReplyUri(Envelope e, IBasicProperties props)
                {
                    var queueName = props.Headers["NServiceBus.ReplyToAddress"];
                    e.ReplyUri = new Uri($"rabbitmq://queue/{queueName}");
                }

                m.MapProperty(x => x.ReplyUri!, ReadReplyUri, WriteReplyToAddress);
            };
        }

        internal IEnvelopeMapper<IBasicProperties, IBasicProperties> BuildMapper(IWolverineRuntime runtime)
        {
            var mapper = new RabbitMqEnvelopeMapper(this, runtime);
            _customizeMapping?.Invoke(mapper);
            if (MessageType != null)
            {
                mapper.ReceivesMessage(MessageType);
            }

            return mapper;
        }
    }
    
}
