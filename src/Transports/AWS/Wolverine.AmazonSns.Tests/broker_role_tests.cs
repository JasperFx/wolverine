using Shouldly;
using Wolverine.AmazonSns.Internal;
using Xunit;

namespace Wolverine.AmazonSns.Tests;

public class broker_role_tests
{
    [Fact]
    public void sns_topic_broker_role_is_topic()
    {
        new AmazonSnsTopic("t", new AmazonSnsTransport()).BrokerRole.ShouldBe("topic");
    }
}
