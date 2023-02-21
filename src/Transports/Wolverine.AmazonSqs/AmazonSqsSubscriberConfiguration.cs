using Amazon.SQS.Model;
using Wolverine.AmazonSqs.Internal;
using Wolverine.Configuration;

namespace Wolverine.AmazonSqs;

public class
    AmazonSqsSubscriberConfiguration : SubscriberConfiguration<AmazonSqsSubscriberConfiguration, AmazonSqsQueue>
{
    internal AmazonSqsSubscriberConfiguration(AmazonSqsQueue endpoint) : base(endpoint)
    {
    }

    /// <summary>
    ///     Configure how the queue should be created within SQS
    /// </summary>
    /// <param name="configure"></param>
    /// <returns></returns>
    public AmazonSqsSubscriberConfiguration ConfigureQueueCreation(Action<CreateQueueRequest> configure)
    {
        add(e => configure(e.Configuration));
        return this;
    }
}