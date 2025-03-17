using Wolverine.AmazonSns.Internal;
using Wolverine.Configuration;

namespace Wolverine.AmazonSns;

public class AmazonSnsListenerConfiguration : ListenerConfiguration<AmazonSnsListenerConfiguration, AmazonSnsTopic>
{
    public AmazonSnsListenerConfiguration(AmazonSnsTopic endpoint) : base(endpoint)
    {
    }
}
