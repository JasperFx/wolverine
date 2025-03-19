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
        var queueOne = transport.Queues["one"];
        var queueTwo = transport.Queues["two"];
        var queueThree = transport.Queues["three"];
        queueThree.IsListener = true;
        
        var topicOne = transport.Topics["one"];
        var topicTwo = transport.Topics["two"];

        queueOne.DeadLetterQueueName = null;
        queueTwo.DeadLetterQueueName = "two-dead-letter-queue";
        queueTwo.IsListener = true;

        var queues = transport.Endpoints().OfType<AmazonSqsQueue>().ToArray();

        queues.ShouldContain(x => x.QueueName == AmazonSqsTransport.DeadLetterQueueName);
        queues.ShouldContain(x => x.QueueName == "two-dead-letter-queue");
        queues.ShouldContain(x => x.QueueName == "one");
        queues.ShouldContain(x => x.QueueName == "two");
        queues.ShouldContain(x => x.QueueName == "three");
        
        var topics = transport.Endpoints().OfType<AmazonSnsTopic>().ToArray();
        topics.ShouldContain(x => x.TopicName == "one");
        topics.ShouldContain(x => x.TopicName == "two");
    }

    [Fact]
    public void findEndpointByUri_should_correctly_find_by_queueName()
    {
        string queueNameInPascalCase = "TestQueue";
        string queueNameLowerCase = "testqueue";
        var transport = new AmazonSqsTransport();
        var testQueue = transport.Queues[queueNameInPascalCase];
        var testQueue2 = transport.Queues[queueNameLowerCase];

        var result = transport.GetOrCreateEndpoint(new Uri($"{AmazonSqsTransport.SqsProtocol}://{AmazonSqsTransport.SqsSegment}/{queueNameInPascalCase}"));
        transport.Queues.Count.ShouldBe(2);

        result.EndpointName.ShouldBe(queueNameInPascalCase);
    }

    [Fact]
    public void findEndpointByUri_should_correctly_find_by_topicName()
    {
        string topicNameInPascalCase = "TestTopic";
        string topicNameLowerCase = "testtopic";
        var transport = new AmazonSqsTransport();
        var testTopic = transport.Topics[topicNameInPascalCase];
        var testTopic2 = transport.Topics[topicNameLowerCase];

        var result = transport.GetOrCreateEndpoint(new Uri($"{AmazonSqsTransport.SqsProtocol}://{AmazonSqsTransport.SqsSegment}/{topicNameInPascalCase}"));
        transport.Topics.Count.ShouldBe(2);

        result.EndpointName.ShouldBe(topicNameInPascalCase);
    }

    [Fact]
    public void findEndpointByUri_should_correctly_create_queue_if_it_doesnt_exist()
    {
        string queueName = "TestQueue";
        string queueName2 = "testqueue";
        var transport = new AmazonSqsTransport();
        transport.Queues.Count.ShouldBe(0);

        var result = transport.GetOrCreateEndpoint(new Uri($"{AmazonSqsTransport.SqsProtocol}://{AmazonSqsTransport.SqsSegment}/{queueName}"));
        transport.Queues.Count.ShouldBe(1);

        result.EndpointName.ShouldBe(queueName);

        var result2 = transport.GetOrCreateEndpoint(new Uri($"{AmazonSqsTransport.SqsProtocol}://{AmazonSqsTransport.SqsSegment}/{queueName2}"));
        transport.Queues.Count.ShouldBe(2);
        result2.EndpointName.ShouldBe(queueName2);
    }
    
    [Fact]
    public void findEndpointByUri_should_correctly_create_topic_if_it_doesnt_exist()
    {
        string topicName = "TestTopic";
        string topicName2 = "testtopic";
        var transport = new AmazonSqsTransport();
        transport.Topics.Count.ShouldBe(0);

        var result = transport.GetOrCreateEndpoint(new Uri($"{AmazonSqsTransport.SqsProtocol}://{AmazonSqsTransport.SnsSegment}/{topicName}"));
        transport.Topics.Count.ShouldBe(1);

        result.EndpointName.ShouldBe(topicName);

        var result2 = transport.GetOrCreateEndpoint(new Uri($"{AmazonSqsTransport.SqsProtocol}://{AmazonSqsTransport.SnsSegment}/{topicName2}"));
        transport.Topics.Count.ShouldBe(2);
        result2.EndpointName.ShouldBe(topicName2);
    }
}
