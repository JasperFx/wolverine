using Wolverine.Configuration;
using Wolverine.Pubsub.Internal;

namespace Wolverine.Pubsub;

public class
    PubsubTopicSubscriberConfiguration : InteroperableSubscriberConfiguration<PubsubTopicSubscriberConfiguration, PubsubTopic, IPubsubEnvelopeMapper, PubsubEnvelopeMapper>
{
    public PubsubTopicSubscriberConfiguration(PubsubTopic endpoint) : base(endpoint)
    {
    }

    /// <summary>
    /// How long Pubsub keeps messages after publishing. The GCP default is 7 days with a minumum of 10 minutes
    /// </summary>
    /// <param name="timespan"></param>
    /// <returns></returns>
    public PubsubTopicSubscriberConfiguration MessageRetentionDuration(TimeSpan timespan)
    {
        add(e => e.MessageRetentionDuration = timespan);
        return this;
    }
}