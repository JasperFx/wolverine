using RabbitMQ.Client;

namespace Wolverine.RabbitMQ.Internal;

public abstract partial class RabbitMqEndpoint
{
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

            void WriteReplyToAddress(Envelope e, IBasicProperties props)
            {
                props.Headers["NServiceBus.ReplyToAddress"] = replyAddress.Value;
            }

            void ReadReplyUri(Envelope e, IBasicProperties props)
            {
                var queueName = props.Headers["NServiceBus.ReplyToAddress"];
                e.ReplyUri = new Uri($"rabbitmq://queue/{queueName}");
            }

            m.MapProperty(x => x.ReplyUri!, ReadReplyUri, WriteReplyToAddress);
        };
    }
}