using Confluent.Kafka;
using Wolverine.Transports;

namespace Wolverine.Kafka;

public interface IKafkaEnvelopeMapper : IEnvelopeMapper<Message<string, string>, Message<string, string>>
{
    
}