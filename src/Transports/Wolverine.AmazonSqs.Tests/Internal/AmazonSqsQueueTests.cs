using Amazon.SQS;
using Amazon.SQS.Model;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Shouldly;
using Wolverine.AmazonSqs.Internal;
using Wolverine.Configuration;

namespace Wolverine.AmazonSqs.Tests.Internal;

public class AmazonSqsQueueTests
{
    [Fact]
    public void default_mode_is_buffered()
    {
        new AmazonSqsQueue("foo", new AmazonSqsTransport())
            .Mode.ShouldBe(EndpointMode.BufferedInMemory);
    }

    [Fact]
    public void default_endpoint_name_is_queue_name()
    {
        new AmazonSqsQueue("foo", new AmazonSqsTransport())
            .EndpointName.ShouldBe("foo");
    }

    [Fact]
    public void uri()
    {
        new AmazonSqsQueue("foo", new AmazonSqsTransport())
            .Uri.ShouldBe(new Uri("sqs://foo"));
    }

    [Fact]
    public void default_visibility_timeout_is_2_minutes()
    {
        new AmazonSqsQueue("foo", new AmazonSqsTransport())
            .VisibilityTimeout.ShouldBe(120);
    }

    [Fact]
    public void default_wait_time_is_5()
    {
        new AmazonSqsQueue("foo", new AmazonSqsTransport())
            .WaitTimeSeconds.ShouldBe(5);
    }

    [Fact]
    public void max_number_of_messages_by_default_is_10()
    {
        new AmazonSqsQueue("foo", new AmazonSqsTransport())
            .MaxNumberOfMessages.ShouldBe(10);
    }

    [Fact]
    public void configure_request()
    {
        var endpoint = new AmazonSqsQueue("foo", new AmazonSqsTransport())
        {
            MaxNumberOfMessages = 8,
            VisibilityTimeout = 3,
            WaitTimeSeconds = 11
        };

        var request = new ReceiveMessageRequest();

        endpoint.ConfigureRequest(request);

        request.VisibilityTimeout.ShouldBe(endpoint.VisibilityTimeout);
        request.MaxNumberOfMessages.ShouldBe(endpoint.MaxNumberOfMessages);
        request.WaitTimeSeconds.ShouldBe(endpoint.WaitTimeSeconds);
    }
}

public class when_initializing_the_endpoint
{
    private readonly IAmazonSQS theClient = Substitute.For<IAmazonSQS>();
    private readonly AmazonSqsQueue theQueue;
    private readonly AmazonSqsTransport theTransport;

    public when_initializing_the_endpoint()
    {
        theTransport = new AmazonSqsTransport(theClient);
        theQueue = new AmazonSqsQueue("foo", theTransport);
    }

    [Fact]
    public async Task do_not_create_if_parent_is_not_auto_provision()
    {
        theTransport.AutoProvision = false;

        var theSqsQueueUrl = "https://someserver.com/foo";

        theClient.GetQueueUrlAsync(theQueue.QueueName).Returns(new GetQueueUrlResponse
        {
            QueueUrl = theSqsQueueUrl
        });

        await theQueue.InitializeAsync(NullLogger.Instance);

        theQueue.QueueUrl.ShouldBe(theSqsQueueUrl);

        await theClient.DidNotReceiveWithAnyArgs().CreateQueueAsync(theQueue.QueueName);
    }

    [Fact]
    public async Task do_create_queue_if_parent_is_autoprovision()
    {
        theTransport.AutoProvision = true;

        var theSqsQueueUrl = "https://someserver.com/foo";

        theClient.CreateQueueAsync(Arg.Any<CreateQueueRequest>())
            .Returns(new CreateQueueResponse
            {
                QueueUrl = theSqsQueueUrl
            });

        await theQueue.InitializeAsync(NullLogger.Instance);

        theQueue.QueueUrl.ShouldBe(theSqsQueueUrl);
    }

    [Fact]
    public async Task do_not_purge_when_not_auto_purge()
    {
        theTransport.AutoPurgeAllQueues = false;
        theTransport.AutoProvision = false;

        // Gotta set this up to make the test work
        var theSqsQueueUrl = "https://someserver.com/foo";
        theClient.GetQueueUrlAsync(theQueue.QueueName).Returns(new GetQueueUrlResponse
        {
            QueueUrl = theSqsQueueUrl
        });

        await theQueue.InitializeAsync(NullLogger.Instance);

        await theClient.DidNotReceiveWithAnyArgs().PurgeQueueAsync(theSqsQueueUrl);
    }

    [Fact]
    public async Task should_purge_when_auto_purge()
    {
        theTransport.AutoPurgeAllQueues = true;
        theTransport.AutoProvision = false;

        // Gotta set this up to make the test work
        var theSqsQueueUrl = "https://someserver.com/foo";
        theClient.GetQueueUrlAsync(theQueue.QueueName).Returns(new GetQueueUrlResponse
        {
            QueueUrl = theSqsQueueUrl
        });

        await theQueue.InitializeAsync(NullLogger.Instance);

        await theClient.Received().PurgeQueueAsync(theSqsQueueUrl);
    }
}