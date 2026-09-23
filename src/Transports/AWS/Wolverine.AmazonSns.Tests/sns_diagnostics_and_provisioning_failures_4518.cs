using Amazon.SimpleNotificationService;
using Amazon.SimpleNotificationService.Model;
using Amazon.SQS;
using Amazon.SQS.Model;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using NSubstitute.ExceptionExtensions;
using Shouldly;
using Wolverine.AmazonSns.Internal;
using Xunit;

namespace Wolverine.AmazonSns.Tests;

/// <summary>
/// GH-4518: two things in Wolverine.AmazonSns that read as Wolverine bugs to a user -- a missing
/// topic reported as a NullReferenceException, and DiagnosticColumns() as a bare
/// NotImplementedException that crashed read-only transport enumeration.
/// </summary>
public class sns_diagnostics_and_provisioning_failures_4518
{
    [Fact]
    public void diagnostic_columns_no_longer_throw()
    {
        var transport = new AmazonSnsTransport();

        // Used to be `throw new NotImplementedException()`, which took down any tooling that
        // enumerated transports on a host that merely registered SNS.
        var columns = transport.DiagnosticColumns().ToArray();

        columns.ShouldNotBeEmpty();
        columns.Select(x => x.Header).ShouldContain("Topic Name");
    }

    [Fact]
    public async Task a_missing_topic_is_an_InvalidOperationException_that_names_the_remedy()
    {
        var snsClient = Substitute.For<IAmazonSimpleNotificationService>();

        // FindTopicAsync() is an SDK extension over ListTopicsAsync; an empty page means "not found".
        snsClient.ListTopicsAsync(Arg.Any<ListTopicsRequest>(), Arg.Any<CancellationToken>())
            .Returns(new ListTopicsResponse { Topics = [], NextToken = null });

        var transport = new AmazonSnsTransport { SnsClient = snsClient };
        var topic = transport.Topics["missing-topic"];

        var ex = await Should.ThrowAsync<InvalidOperationException>(async () => await topic.GetAttributesAsync());

        ex.ShouldNotBeOfType<NullReferenceException>();
        ex.Message.ShouldContain("missing-topic");
        ex.Message.ShouldContain("AutoProvision()");
        ex.Message.ShouldContain("UseAmazonSnsTransport()");
    }

    [Fact]
    public async Task a_missing_subscribed_queue_names_the_queue_and_the_ordering_requirement()
    {
        var snsClient = Substitute.For<IAmazonSimpleNotificationService>();
        snsClient.CreateTopicAsync(Arg.Any<CreateTopicRequest>(), Arg.Any<CancellationToken>())
            .Returns(new CreateTopicResponse { TopicArn = "arn:aws:sns:us-east-1:123456789012:notifications" });

        var sqsClient = Substitute.For<IAmazonSQS>();
        sqsClient.GetQueueUrlAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Throws(new QueueDoesNotExistException("The specified queue does not exist."));

        var transport = new AmazonSnsTransport { SnsClient = snsClient, SqsClient = sqsClient };
        transport.AutoProvision = true;

        var topic = transport.Topics["notifications"];
        topic.TopicSubscriptions.Add(new AmazonSnsSubscription("downstream-queue", AmazonSnsSubscriptionType.Sqs,
            new AmazonSnsSubscriptionAttributes()));

        var ex = await Should.ThrowAsync<WolverineSnsTransportException>(async () =>
            await topic.InitializeAsync(NullLogger.Instance));

        var inner = ex.InnerException.ShouldBeOfType<InvalidOperationException>();
        inner.Message.ShouldContain("downstream-queue");
        inner.Message.ShouldContain("notifications");
        inner.Message.ShouldContain("must be created before");
    }
}
