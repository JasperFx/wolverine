using System.Text;
using Newtonsoft.Json;
using RabbitMQ.Client;
using Wolverine.Runtime.Serialization;

namespace Wolverine.RabbitMQ.Internal;

public abstract partial class RabbitMqEndpoint
{
    public void UseNServiceBusInterop()
    {
        DefaultSerializer = new NewtonsoftSerializer(new JsonSerializerSettings());
        
        _customizeMapping = m =>
        {
            m.MapPropertyToHeader(x => x.ConversationId, "NServiceBus.ConversationId");
            m.MapPropertyToHeader(x => x.SentAt, "NServiceBus.TimeSent");
            m.MapPropertyToHeader(x => x.CorrelationId!, "NServiceBus.CorrelationId");

            var replyAddress = new Lazy<string>(() =>
            {
                var replyEndpoint = (RabbitMqEndpoint)_parent.ReplyEndpoint()!;
                return replyEndpoint.RoutingKey();
            });

            void WriteReplyToAddress(Envelope e, IBasicProperties props)
            {
                props.Headers["NServiceBus.ReplyToAddress"] = replyAddress.Value;
            }

            void ReadReplyUri(Envelope e, IReadOnlyBasicProperties props)
            {
                if (props.Headers.TryGetValue("NServiceBus.ReplyToAddress", out var raw))
                {
                    var queueName = (raw is byte[] b ? Encoding.Default.GetString(b) : raw.ToString())!;
                    e.ReplyUri = new Uri($"{_parent.Protocol}://queue/{queueName}");
                }
            }

            m.MapProperty(x => x.ReplyUri!, ReadReplyUri, WriteReplyToAddress);
        };
    }
}