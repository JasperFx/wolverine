using Wolverine.AmazonSqs.Internal;
using Wolverine.Configuration;

namespace Wolverine.AmazonSqs;

public class AmazonSqsListenerConfiguration : ListenerConfiguration<AmazonSqsListenerConfiguration, AmazonSqsQueue>
{
    internal AmazonSqsListenerConfiguration(AmazonSqsQueue queue) : base(queue)
    {
    }
    
}