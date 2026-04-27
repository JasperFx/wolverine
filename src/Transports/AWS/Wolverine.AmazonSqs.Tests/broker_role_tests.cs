using Shouldly;
using Wolverine.AmazonSqs.Internal;
using Xunit;

namespace Wolverine.AmazonSqs.Tests;

public class broker_role_tests
{
    [Fact]
    public void sqs_queue_broker_role_is_queue()
    {
        new AmazonSqsQueue("q", new AmazonSqsTransport()).BrokerRole.ShouldBe("queue");
    }
}
