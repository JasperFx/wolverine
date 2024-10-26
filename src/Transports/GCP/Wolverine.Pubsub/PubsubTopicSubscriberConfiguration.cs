using Wolverine.Configuration;

namespace Wolverine.Pubsub;

public class
    PubsubTopicSubscriberConfiguration : SubscriberConfiguration<PubsubTopicSubscriberConfiguration, PubsubEndpoint>
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

    /// <summary>
    ///     Utilize custom envelope mapping for Google Cloud Platform Pub/Sub interoperability with external non-Wolverine
    ///     systems
    /// </summary>
    /// <param name="mapper"></param>
    /// <returns></returns>
    public PubsubTopicSubscriberConfiguration InteropWith(IPubsubEnvelopeMapper mapper)
    {
        add(e => e.Mapper = mapper);

        return this;
    }

    /// <summary>
    ///     Utilize custom envelope mapping for Google Cloud Platform Pub/Sub interoperability with external non-Wolverine
    ///     systems
    /// </summary>
    /// <param name="mapper"></param>
    /// <returns></returns>
    public PubsubTopicSubscriberConfiguration InteropWith(Func<PubsubEndpoint, IPubsubEnvelopeMapper> mapper)
    {
        add(e => e.Mapper = mapper(e));

        return this;
    }
}