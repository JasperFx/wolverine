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

    /// <summary>
    ///     Derive the Google Cloud Pub/Sub <c>OrderingKey</c> for each outgoing message on this topic from
    ///     the outgoing <see cref="Envelope" />. Return <c>null</c> to leave the message unkeyed.
    ///     <para>
    ///     The ordering key is resolved with a three step precedence: an explicit
    ///     <see cref="Envelope.GroupId" /> wins, then the function supplied here, and finally whatever a
    ///     custom <see cref="IPubsubEnvelopeMapper" /> already stamped onto the message. This is therefore
    ///     the per-topic escape hatch for "key on something other than the group id" without having to
    ///     replace the envelope mapper.
    ///     </para>
    ///     <para>
    ///     Be aware of the throughput cost. Pub/Sub serializes delivery per ordering key, so any message
    ///     that carries one — by this route or any other — caps the consumer's effective concurrency at the
    ///     number of distinct keys in flight, regardless of how <c>MaxOutstandingMessages</c> is sized.
    ///     </para>
    /// </summary>
    /// <param name="orderBy">Function returning the ordering key for an outgoing envelope, or null for none</param>
    /// <returns></returns>
    /// <exception cref="ArgumentNullException"></exception>
    public PubsubTopicSubscriberConfiguration OrderMessagesBy(Func<Envelope, string?> orderBy)
    {
        if (orderBy is null)
        {
            throw new ArgumentNullException(nameof(orderBy));
        }

        add(e => e.Server.Topic.OrderBy = orderBy);

        return this;
    }
}