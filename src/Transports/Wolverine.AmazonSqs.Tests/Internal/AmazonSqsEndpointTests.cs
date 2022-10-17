using Amazon.SQS;
using Amazon.SQS.Model;
using NSubstitute;
using Shouldly;
using Wolverine.AmazonSqs.Internal;
using Wolverine.Configuration;

namespace Wolverine.AmazonSqs.Tests.Internal;

public class AmazonSqsEndpointTests
{
    [Fact]
    public void default_mode_is_buffered()
    {
        new AmazonSqsEndpoint("foo",new AmazonSqsTransport())
            .Mode.ShouldBe(EndpointMode.BufferedInMemory);
    }

    [Fact]
    public void default_visibility_timeout_is_2_minutes()
    {
        new AmazonSqsEndpoint("foo",new AmazonSqsTransport())
            .VisibilityTimeout.ShouldBe(120);
    }

    [Fact]
    public void default_wait_time_is_5()
    {
        new AmazonSqsEndpoint("foo",new AmazonSqsTransport())
            .WaitTimeSeconds.ShouldBe(5);
    }

    [Fact]
    public void max_number_of_messages_by_default_is_10()
    {
        new AmazonSqsEndpoint("foo",new AmazonSqsTransport())
            .MaxNumberOfMessages.ShouldBe(10);
    }

    [Fact]
    public void configure_request()
    {
        var endpoint = new AmazonSqsEndpoint("foo", new AmazonSqsTransport())
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
    private readonly AmazonSqsTransport theTransport;
    private readonly AmazonSqsEndpoint theEndpoint;

    public when_initializing_the_endpoint()
    {   
        theTransport = new AmazonSqsTransport(theClient);
        theEndpoint = new AmazonSqsEndpoint("foo", theTransport);
    }

    [Fact]
    public async Task do_not_create_if_parent_is_not_auto_provision()
    {
        theTransport.AutoProvision = false;

        var theSqsQueueUrl = "https://someserver.com/foo";

        theClient.GetQueueUrlAsync(theEndpoint.QueueName).Returns(new GetQueueUrlResponse
        {
            QueueUrl = theSqsQueueUrl
        });

        await theEndpoint.InitializeAsync();

        theEndpoint.QueueUrl.ShouldBe(theSqsQueueUrl);

        await theClient.DidNotReceiveWithAnyArgs().CreateQueueAsync(theEndpoint.QueueName);
    }

    [Fact]
    public async Task do_create_queue_if_parent_is_autoprovision()
    {
        theTransport.AutoProvision = true;
        
        var theSqsQueueUrl = "https://someserver.com/foo";

        theClient.CreateQueueAsync(theEndpoint.QueueName)
            .Returns(new CreateQueueResponse
            {
                QueueUrl = theSqsQueueUrl
            });
        
        await theEndpoint.InitializeAsync();

        theEndpoint.QueueUrl.ShouldBe(theSqsQueueUrl);
    }

    [Fact]
    public async Task do_not_purge_when_not_auto_purge()
    {
        theTransport.AutoPurgeOnStartup = false;
        theTransport.AutoProvision = false;

        // Gotta set this up to make the test work
        var theSqsQueueUrl = "https://someserver.com/foo";
        theClient.GetQueueUrlAsync(theEndpoint.QueueName).Returns(new GetQueueUrlResponse
        {
            QueueUrl = theSqsQueueUrl
        });

        await theEndpoint.InitializeAsync();

        await theClient.DidNotReceiveWithAnyArgs().PurgeQueueAsync(theSqsQueueUrl);
    }

    [Fact]
    public async Task should_purge_when_auto_purge()
    {
        theTransport.AutoPurgeOnStartup = true;
        theTransport.AutoProvision = false;

        // Gotta set this up to make the test work
        var theSqsQueueUrl = "https://someserver.com/foo";
        theClient.GetQueueUrlAsync(theEndpoint.QueueName).Returns(new GetQueueUrlResponse
        {
            QueueUrl = theSqsQueueUrl
        });

        await theEndpoint.InitializeAsync();

        await theClient.Received().PurgeQueueAsync(theSqsQueueUrl);
    }

}