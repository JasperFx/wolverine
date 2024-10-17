using Wolverine.Pubsub.Internal;
using Wolverine.Configuration;

namespace Wolverine.Pubsub;

public class PubsubTopicConfiguration : SubscriberConfiguration<PubsubTopicConfiguration, PubsubTopic> {
    public PubsubTopicConfiguration(PubsubTopic endpoint) : base(endpoint) { }

    /// <summary>
    /// Utilize custom envelope mapping for Google Cloud Pub/Sub interoperability with external non-Wolverine systems
    /// </summary>
    /// <param name="mapper"></param>
    /// <returns></returns>
    public PubsubTopicConfiguration InteropWith(IPubsubEnvelopeMapper mapper) {
        add(e => e.Mapper = mapper);

        return this;
    }
}
