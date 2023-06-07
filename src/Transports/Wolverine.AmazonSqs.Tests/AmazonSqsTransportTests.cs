using Shouldly;
using Wolverine.AmazonSqs.Internal;

namespace Wolverine.AmazonSqs.Tests;

public class AmazonSqsTransportTests
{
    [Theory]
    [InlineData("good.fifo", "good.fifo")]
    [InlineData("good", "good")]
    [InlineData("foo.bar", "foo-bar")]
    [InlineData("foo.bar.fifo", "foo-bar.fifo")]
    public void sanitizing_identifiers(string identifier, string expected)
    {
        new AmazonSqsTransport().SanitizeIdentifier(identifier)
            .ShouldBe(expected);
    }

    [Fact]
    public void return_all_endpoints_gets_dead_letter_queue_too()
    {
        var transport = new AmazonSqsTransport();
        var one = transport.Queues["one"];
        var two = transport.Queues["two"];
        var three = transport.Queues["three"];

        one.DeadLetterQueueName = null;
        two.DeadLetterQueueName = "two-dead-letter-queue";

        var endpoints = transport.Endpoints().OfType<AmazonSqsQueue>().ToArray();
        
        endpoints.ShouldContain(x => x.QueueName == AmazonSqsTransport.DeadLetterQueueName);
        endpoints.ShouldContain(x => x.QueueName == "two-dead-letter-queue");
        endpoints.ShouldContain(x => x.QueueName == "one");
        endpoints.ShouldContain(x => x.QueueName == "two");
        endpoints.ShouldContain(x => x.QueueName == "three");
    }
}