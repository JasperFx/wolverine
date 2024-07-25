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
        three.IsListener = true;

        one.DeadLetterQueueName = null;
        two.DeadLetterQueueName = "two-dead-letter-queue";
        two.IsListener = true;

        var endpoints = transport.Endpoints().OfType<AmazonSqsQueue>().ToArray();

        endpoints.ShouldContain(x => x.QueueName == AmazonSqsTransport.DeadLetterQueueName);
        endpoints.ShouldContain(x => x.QueueName == "two-dead-letter-queue");
        endpoints.ShouldContain(x => x.QueueName == "one");
        endpoints.ShouldContain(x => x.QueueName == "two");
        endpoints.ShouldContain(x => x.QueueName == "three");
    }

    [Fact]
    public void findEndpointByUri_should_correctly_find_by_queuename()
    {
        string queueNameInPascalCase = "TestQueue";
        string queueNameLowerCase = "testqueue";
        var transport = new AmazonSqsTransport();
        var testQueue = transport.Queues[queueNameInPascalCase];
        var testQueue2 = transport.Queues[queueNameLowerCase];

        var result = transport.GetOrCreateEndpoint(new Uri($"sqs://{queueNameInPascalCase}"));
        transport.Queues.Count.ShouldBe(2);

        result.EndpointName.ShouldBe(queueNameInPascalCase);
    }

    [Fact]
    public void findEndpointByUri_should_correctly_create_endpoint_if_it_doesnt_exist()
    {
        string queueName = "TestQueue";
        string queueName2 = "testqueue";
        var transport = new AmazonSqsTransport();
        transport.Queues.Count.ShouldBe(0);

        var result = transport.GetOrCreateEndpoint(new Uri($"sqs://{queueName}"));
        transport.Queues.Count.ShouldBe(1);

        result.EndpointName.ShouldBe(queueName);

        var result2 = transport.GetOrCreateEndpoint(new Uri($"sqs://{queueName2}"));
        transport.Queues.Count.ShouldBe(2);
        result2.EndpointName.ShouldBe(queueName2);
    }
}