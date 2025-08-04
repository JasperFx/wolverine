using Amazon.SimpleNotificationService;
using Amazon.SimpleNotificationService.Model;
using Amazon.SQS;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Shouldly;
using Wolverine.AmazonSns.Internal;
using Wolverine.Configuration;

namespace Wolverine.AmazonSns.Tests.Internal;

public class AmazonSnsTopicTests
{
    [Fact]
    public void default_mode_is_buffered()
    {
        new AmazonSnsTopic("foo", new AmazonSnsTransport())
            .Mode.ShouldBe(EndpointMode.BufferedInMemory);
    }
    
    [Fact]
    public void uri()
    {
        new AmazonSnsTopic("foo", new AmazonSnsTransport())
            .Uri.ShouldBe(new Uri("sns://foo"));
    }
    
    [Fact]
    public void message_batch_size_by_default_is_10()
    {
        new AmazonSnsTopic("foo", new AmazonSnsTransport())
            .MessageBatchSize.ShouldBe(10);
    }
}

public class when_initializing_the_endpoint
{
    private readonly IAmazonSimpleNotificationService theSnsClient = Substitute.For<IAmazonSimpleNotificationService>();
    private readonly IAmazonSQS theSqsClient = Substitute.For<IAmazonSQS>();
    private readonly AmazonSnsTopic theTopic;
    private readonly AmazonSnsTransport theTransport;
    
    public when_initializing_the_endpoint()
    {
        theTransport = new AmazonSnsTransport(theSnsClient, theSqsClient);
        theTopic = new AmazonSnsTopic("foo", theTransport);
    }
    
    [Fact]
    public async Task do_not_create_if_parent_is_not_auto_provision()
    {
        theTransport.AutoProvision = false;

        const string theSnsTopicArn = "arn:aws:sns:us-east-2:123456789012:TheTopic";

        theSnsClient.FindTopicAsync(theTopic.TopicName).Returns(new Topic
        {
            TopicArn = theSnsTopicArn
        });

        await theTopic.InitializeAsync(NullLogger.Instance);

        theTopic.TopicArn.ShouldBe(theSnsTopicArn);

        await theSnsClient.DidNotReceiveWithAnyArgs().CreateTopicAsync(theTopic.TopicName);
    }
    
    [Fact]
    public async Task do_create_topic_if_parent_is_auto_provision()
    {
        theTransport.AutoProvision = true;

        const string theSnsTopicArn = "arn:aws:sns:us-east-2:123456789012:TheTopic";

        theSnsClient.CreateTopicAsync(Arg.Any<CreateTopicRequest>())
            .Returns(new CreateTopicResponse
            {
                TopicArn = theSnsTopicArn
            });

        await theTopic.InitializeAsync(NullLogger.Instance);

        theTopic.TopicArn.ShouldBe(theSnsTopicArn);
    }

}
