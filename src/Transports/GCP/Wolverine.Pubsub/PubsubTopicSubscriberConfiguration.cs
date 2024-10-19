using Wolverine.Pubsub.Internal;
using Wolverine.Configuration;

namespace Wolverine.Pubsub;

public class PubsubTopicSubscriberConfiguration : SubscriberConfiguration<PubsubTopicSubscriberConfiguration, PubsubEndpoint> {
    public PubsubTopicSubscriberConfiguration(PubsubEndpoint endpoint) : base(endpoint) { }

    /// <summary>
    /// Utilize custom envelope mapping for Google Cloud Pub/Sub interoperability with external non-Wolverine systems
    /// </summary>
    /// <param name="mapper"></param>
    /// <returns></returns>
    public PubsubTopicSubscriberConfiguration InteropWith(IPubsubEnvelopeMapper mapper) {
        add(e => e.Mapper = mapper);

        return this;
    }
}
