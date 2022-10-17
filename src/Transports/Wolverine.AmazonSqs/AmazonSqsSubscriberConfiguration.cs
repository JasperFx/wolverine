using Wolverine.AmazonSqs.Internal;
using Wolverine.Configuration;

namespace Wolverine.AmazonSqs;

public class AmazonSqsSubscriberConfiguration : SubscriberConfiguration<AmazonSqsSubscriberConfiguration, AmazonSqsEndpoint>
{
    internal AmazonSqsSubscriberConfiguration(AmazonSqsEndpoint endpoint) : base(endpoint)
    {
    }
}