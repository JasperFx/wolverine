using Google.Cloud.PubSub.V1;
using Wolverine.Transports;

namespace Wolverine.Pubsub;

public interface IPubsubEnvelopeMapper : IEnvelopeMapper<PubsubMessage, PubsubMessage>;
