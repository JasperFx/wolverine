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