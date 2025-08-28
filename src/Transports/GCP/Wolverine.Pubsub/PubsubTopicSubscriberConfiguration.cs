using Wolverine.Configuration;
using Wolverine.Pubsub.Internal;

namespace Wolverine.Pubsub;

public class
    PubsubTopicSubscriberConfiguration : InteroperableSubscriberConfiguration<PubsubTopicSubscriberConfiguration, PubsubEndpoint, IPubsubEnvelopeMapper, PubsubEnvelopeMapper>
{
    public PubsubTopicSubscriberConfiguration(PubsubEndpoint endpoint) : base(endpoint)
    {
    }

    /// <summary>
    ///     Configure the underlying Google Cloud Platform Pub/Sub topic. This is only applicable when
    ///     Wolverine is creating the topic.
    /// </summary>
    /// <param name="configure"></param>
    /// <returns></returns>
    public PubsubTopicSubscriberConfiguration ConfigurePubsubTopic(Action<CreateTopicOptions> configure)
    {
        add(e => configure(e.Server.Topic.Options));

        return this;
    }
}