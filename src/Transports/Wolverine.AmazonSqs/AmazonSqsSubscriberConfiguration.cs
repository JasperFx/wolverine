using Wolverine.AmazonSqs.Internal;
using Wolverine.Configuration;

namespace Wolverine.AmazonSqs;

public class AmazonSqsSubscriberConfiguration : SubscriberConfiguration<AmazonSqsSubscriberConfiguration, AmazonSqsQueue>
{
    internal AmazonSqsSubscriberConfiguration(AmazonSqsQueue queue) : base(queue)
    {
    }
}