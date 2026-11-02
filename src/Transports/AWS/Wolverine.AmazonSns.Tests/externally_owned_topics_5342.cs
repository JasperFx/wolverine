using Amazon.Runtime;
using Amazon.SimpleNotificationService;
using Amazon.SimpleNotificationService.Model;
using Amazon.SQS;
using Amazon.SQS.Model;
using JasperFx;
using JasperFx.Core;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Shouldly;
using Wolverine.AmazonSns.Internal;
using Wolverine.AmazonSqs;
using Wolverine.Tracking;
using Xunit;

namespace Wolverine.AmazonSns.Tests;

/// <summary>
/// GH-5342. A service that subscribes its own SQS queue to an SNS topic another service owns wants Wolverine to
/// create its queue and its subscription, but never to create, change or delete the topic.
/// </summary>
public class externally_owned_topics_5342 : IAsyncLifetime
{
    private const string OwnersTopic = "gh5342-owners-orders";
    private const string OwnQueue = "gh5342-shipping";

    private static readonly BasicAWSCredentials Credentials = new("ignore", "ignore");

    private readonly AmazonSimpleNotificationServiceClient theSns = new(Credentials,
        new AmazonSimpleNotificationServiceConfig { ServiceURL = "http://localhost:4566" });

    private readonly AmazonSQSClient theSqs = new(Credentials,
        new AmazonSQSConfig { ServiceURL = "http://localhost:4566" });

    public async ValueTask InitializeAsync()
    {
        await deleteAllAsync();
    }

    public async ValueTask DisposeAsync()
    {
        await deleteAllAsync();
        theSns.Dispose();
        theSqs.Dispose();
    }

    [Fact]
    public async Task an_externally_owned_topic_is_not_changed_but_the_subscription_is_provisioned()
    {
        var ct = TestContext.Current.CancellationToken;

        // The publishing service owns the topic, and created it its own way
        var arn = (await theSns.CreateTopicAsync(new CreateTopicRequest(OwnersTopic)
        {
            Attributes = new Dictionary<string, string> { ["DisplayName"] = "Owned by orders" }
        }, ct)).TopicArn;

        using var host = await Host.CreateDefaultBuilder()
            .UseWolverine(opts =>
            {
                // Wolverine still provisions everything this application owns...
                opts.UseAmazonSqsTransportLocally().AutoProvision();
                opts.UseAmazonSnsTransportLocally().AutoProvision();

                opts.ListenToSqsQueue(OwnQueue).ReceiveSnsTopicMessage();

                #region sample_sns_topic_is_externally_owned

                opts.PublishMessage<OrderShipped>()
                    .ToSnsTopic("gh5342-owners-orders")

                    // The topic belongs to the service that owns orders. Wolverine never
                    // creates, changes or deletes it, but it still subscribes this
                    // application's own queue to it
                    .ExternallyOwned()
                    .SubscribeSqsQueue("gh5342-shipping");

                #endregion
            }).StartAsync(ct);

        var session = await host.TrackActivity()
            .IncludeExternalTransports()
            .Timeout(30.Seconds())
            .SendMessageAndWaitAsync(new OrderShipped("one"));

        session.Received.SingleMessage<OrderShipped>().Id.ShouldBe("one");

        // Still the owner's topic
        var attributes = await theSns.GetTopicAttributesAsync(arn, ct);
        attributes.Attributes["DisplayName"].ShouldBe("Owned by orders");
    }

    [Fact]
    public async Task resources_teardown_leaves_an_externally_owned_topic_alone()
    {
        await theSns.CreateTopicAsync(OwnersTopic, TestContext.Current.CancellationToken);

        IHostBuilder configure() => Host.CreateDefaultBuilder()
            .UseWolverine(opts =>
            {
                opts.UseAmazonSnsTransportLocally();

                opts.PublishMessage<OrderShipped>().ToSnsTopic(OwnersTopic).ExternallyOwned();
            });

        (await configure().RunJasperFxCommands(["resources", "check"])).ShouldBe(0);
        (await configure().RunJasperFxCommands(["resources", "teardown"])).ShouldBe(0);

        (await theSns.FindTopicAsync(OwnersTopic)).ShouldNotBeNull();
    }

    [Fact]
    public async Task a_missing_externally_owned_topic_is_named_and_not_created()
    {
        var snsClient = Substitute.For<IAmazonSimpleNotificationService>();

        // FindTopicAsync() is an SDK extension over ListTopicsAsync; an empty page means "not found"
        snsClient.ListTopicsAsync(Arg.Any<ListTopicsRequest>(), Arg.Any<CancellationToken>())
            .Returns(new ListTopicsResponse { Topics = [], NextToken = null });

        var transport = new AmazonSnsTransport { SnsClient = snsClient, AutoProvision = true };
        var topic = transport.Topics["payments"];
        topic.IsExternallyOwned = true;

        var ex = await Should.ThrowAsync<WolverineSnsTransportException>(async () =>
            await topic.InitializeAsync(NullLogger.Instance));

        var inner = ex.InnerException.ShouldBeOfType<InvalidOperationException>();
        inner.Message.ShouldContain("'payments'");
        inner.Message.ShouldContain("externally owned");

        await snsClient.DidNotReceive().CreateTopicAsync(Arg.Any<CreateTopicRequest>(), Arg.Any<CancellationToken>());
    }

    private async Task deleteAllAsync()
    {
        var topic = await theSns.FindTopicAsync(OwnersTopic);
        if (topic != null)
        {
            await theSns.DeleteTopicAsync(topic.TopicArn);
        }

        try
        {
            var url = (await theSqs.GetQueueUrlAsync(OwnQueue)).QueueUrl;
            await theSqs.DeleteQueueAsync(url);
        }
        catch (QueueDoesNotExistException)
        {
            // Already gone
        }
    }
}

public record OrderShipped(string Id);

public static class OrderShippedHandler
{
    public static void Handle(OrderShipped message)
    {
    }
}
