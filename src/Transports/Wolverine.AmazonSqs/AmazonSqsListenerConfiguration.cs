using Wolverine.AmazonSqs.Internal;
using Wolverine.Configuration;

namespace Wolverine.AmazonSqs;

public class AmazonSqsListenerConfiguration : SubscriberConfiguration<AmazonSqsListenerConfiguration, AmazonSqsEndpoint>
{
    internal AmazonSqsListenerConfiguration(AmazonSqsEndpoint endpoint) : base(endpoint)
    {
    }
}