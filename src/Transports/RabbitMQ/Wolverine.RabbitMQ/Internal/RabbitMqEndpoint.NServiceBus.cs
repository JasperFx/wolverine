using System.Text;
using Newtonsoft.Json;
using RabbitMQ.Client;
using Wolverine.Configuration;
using Wolverine.Newtonsoft;
using Wolverine.Transports;

namespace Wolverine.RabbitMQ.Internal;

public abstract partial class RabbitMqEndpoint
{
    #region sample_show_the_nservicebus_mapping
    public void UseNServiceBusInterop()
    {
        // We haven't tried to address this yet, but NSB can stick in some characters
        // that STJ chokes on, but good ol' Newtonsoft handles just fine
        //
        // GH-5244: XML bodies are read too, and sent instead of JSON when the endpoint's default serializer is
        // an NServiceBusXmlSerializer
        NServiceBusInterop.UseNServiceBusSerializers(this, new NewtonsoftSerializer(new JsonSerializerSettings()));

        customizeMapping((m, _) =>
        {
            m.MapPropertyToHeader(x => x.ConversationId, "NServiceBus.ConversationId");
            m.MapPropertyToHeader(x => x.SentAt, "NServiceBus.TimeSent");
            m.MapPropertyToHeader(x => x.CorrelationId!, "NServiceBus.CorrelationId");

            var replyAddress = new Lazy<string>(() =>
            {
                // NServiceBus replies through an exchange named after this address, which Wolverine's own
                // system response queue never has. Its system queue is also flagged for replies, so an
                // application queue marked with UseForReplies() has to win over it explicitly
                var replyEndpoint = _parent.Queues
                                        .FirstOrDefault(x => x is { IsListener: true, IsUsedForReplies: true, Role: EndpointRole.Application })
                                    ?? (RabbitMqEndpoint)_parent.ReplyEndpoint()!;
                return replyEndpoint.RoutingKey();
            });

            void WriteReplyToAddress(Envelope e, IBasicProperties props)
            {
                props.Headers!["NServiceBus.ReplyToAddress"] = replyAddress.Value;
            }

            void ReadReplyUri(Envelope e, IReadOnlyBasicProperties props)
            {
                if (props.Headers!.TryGetValue("NServiceBus.ReplyToAddress", out var raw))
                {
                    var queueName = (raw is byte[] b ? Encoding.UTF8.GetString(b) : raw!.ToString())!;
                    e.ReplyUri = new Uri($"{_parent.Protocol}://queue/{queueName}");
                }
            }

            m.MapProperty(x => x.ReplyUri!, ReadReplyUri, WriteReplyToAddress);

            // NServiceBus picks its deserializer from the NServiceBus.ContentType header, so stamp it next to
            // the native content type, and prefer it on the way in
            void ReadContentType(Envelope e, IReadOnlyBasicProperties props)
            {
                e.ContentType = props.Headers != null &&
                                props.Headers.TryGetValue(NServiceBusInterop.ContentTypeHeader, out var raw) &&
                                raw != null
                    ? raw is byte[] b ? Encoding.UTF8.GetString(b) : raw.ToString()
                    : props.ContentType;
            }

            void WriteContentType(Envelope e, IBasicProperties props)
            {
                props.ContentType = e.ContentType;
                if (e.ContentType != null)
                {
                    props.Headers![NServiceBusInterop.ContentTypeHeader] = e.ContentType;
                }
            }

            m.MapProperty(x => x.ContentType!, ReadContentType, WriteContentType);
        });
    }

    #endregion
}