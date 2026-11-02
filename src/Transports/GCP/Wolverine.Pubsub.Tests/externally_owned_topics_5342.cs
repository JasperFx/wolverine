using Google.Api.Gax;
using Google.Cloud.PubSub.V1;
using Grpc.Core;
using JasperFx;
using JasperFx.Core;
using Microsoft.Extensions.Hosting;
using Shouldly;
using Xunit;
using Wolverine.Transports;

namespace Wolverine.Pubsub.Tests;

/// <summary>
/// GH-5342. A service that listens to a topic another service publishes to wants Wolverine to provision its own
/// subscription, but never the topic, and to leave other services' topics and subscriptions alone in
/// <c>resources setup</c> and <c>resources teardown</c>.
/// </summary>
public class externally_owned_topics_5342 : IAsyncLifetime
{
    private const string ProjectId = "wolverine";

    // Unique per run, so these tests never meet topics left behind by another suite on the shared emulator
    private readonly string theSuffix = Guid.NewGuid().ToString("N")[..8];

    private PublisherServiceApiClient thePublisher = null!;
    private SubscriberServiceApiClient theSubscriber = null!;
    private bool _skip;

    public async ValueTask InitializeAsync()
    {
        _skip = !await TestingExtensions.IsEmulatorAvailable();
        if (_skip) return;

        Environment.SetEnvironmentVariable("PUBSUB_EMULATOR_HOST", TestingExtensions.EmulatorHost);

        thePublisher = await new PublisherServiceApiClientBuilder { EmulatorDetection = EmulatorDetection.EmulatorOnly }
            .BuildAsync();
        theSubscriber = await new SubscriberServiceApiClientBuilder { EmulatorDetection = EmulatorDetection.EmulatorOnly }
            .BuildAsync();
    }

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;

    private string name(string prefix) => $"{prefix}-{theSuffix}";

    private Task createTopicAsync(string topic, TimeSpan? retention = null)
    {
        var request = new Topic { TopicName = new TopicName(ProjectId, topic) };
        if (retention.HasValue)
        {
            request.MessageRetentionDuration = Google.Protobuf.WellKnownTypes.Duration.FromTimeSpan(retention.Value);
        }

        return thePublisher.CreateTopicAsync(request);
    }

    private async Task<bool> topicExistsAsync(string topic)
    {
        try
        {
            await thePublisher.GetTopicAsync(new TopicName(ProjectId, topic));
            return true;
        }
        catch (RpcException e) when (e.StatusCode == StatusCode.NotFound)
        {
            return false;
        }
    }

    private async Task<bool> subscriptionExistsAsync(string subscription)
    {
        try
        {
            await theSubscriber.GetSubscriptionAsync(new SubscriptionName(ProjectId, subscription));
            return true;
        }
        catch (RpcException e) when (e.StatusCode == StatusCode.NotFound)
        {
            return false;
        }
    }

    [Fact]
    public async Task an_externally_owned_topic_is_not_changed_but_the_subscription_is_provisioned()
    {
        if (_skip) return;

        var orders = name("orders");

        // The publishing service owns the topic, and created it its own way
        await createTopicAsync(orders, 2.Hours());

        using var host = await Host.CreateDefaultBuilder()
            .UseWolverine(opts =>
            {
                // Wolverine still provisions everything this application owns...
                opts.UsePubsubTesting().AutoProvision();

                #region sample_pubsub_topic_is_externally_owned

                opts.ListenToPubsubTopic(orders)

                    // The topic belongs to the service that publishes to it. Wolverine
                    // creates this listener's subscription on that topic, but never
                    // creates or deletes the topic itself
                    .TopicIsExternallyOwned();

                #endregion
            }).StartAsync(TestContext.Current.CancellationToken);

        (await subscriptionExistsAsync(orders)).ShouldBeTrue();

        // Still the owner's topic
        var topic = await thePublisher.GetTopicAsync(new TopicName(ProjectId, orders));
        topic.MessageRetentionDuration.ToTimeSpan().ShouldBe(2.Hours());
    }

    [Fact]
    public async Task a_missing_externally_owned_topic_is_named_and_not_created()
    {
        if (_skip) return;

        var payments = name("payments");

        var builder = Host.CreateDefaultBuilder()
            .UseWolverine(opts =>
            {
                // Give up after the first attempt rather than retrying for two minutes
                opts.BrokerInitializationTimeout = 1.Seconds();

                opts.UsePubsubTesting().AutoProvision();

                opts.ListenToPubsubTopic(payments).TopicIsExternallyOwned();
            });

        var ex = await Should.ThrowAsync<Exception>(async () =>
        {
            using var host = await builder.StartAsync(TestContext.Current.CancellationToken);
        });

        var notFound = findInChain<ExternallyOwnedObjectNotFoundException>(ex);
        notFound.ShouldNotBeNull();
        notFound.ObjectName.ShouldBe(new TopicName(ProjectId, payments).ToString());
        notFound.Message.ShouldContain(payments);

        (await topicExistsAsync(payments)).ShouldBeFalse();
    }

    [Fact]
    public async Task resources_setup_and_teardown_leave_externally_owned_objects_alone()
    {
        if (_skip) return;

        var events = name("events");
        var audit = name("audit");
        var inbox = name("inbox");
        var notifications = name("notifications");

        // Owned elsewhere: two topics, and a topic with a subscription this service only reads from
        await createTopicAsync(events);
        await createTopicAsync(audit);
        await createTopicAsync(inbox);
        await theSubscriber.CreateSubscriptionAsync(new Subscription
        {
            SubscriptionName = new SubscriptionName(ProjectId, inbox),
            TopicAsTopicName = new TopicName(ProjectId, inbox)
        });

        IHostBuilder configure() => Host.CreateDefaultBuilder()
            .UseWolverine(opts =>
            {
                opts.UsePubsubTesting();

                opts.ListenToPubsubTopic(events).TopicIsExternallyOwned();

                #region sample_pubsub_topics_and_subscriptions_are_externally_owned

                // A topic another service owns and this one only publishes to
                opts.PublishMessage<PubsubAuditEntryRecorded>()
                    .ToPubsubTopic(audit)
                    .ExternallyOwned();

                // A topic and subscription another service owns, which this one only reads from
                opts.ListenToPubsubTopic(inbox)
                    .ExternallyOwned();

                #endregion

                // Owned by this application, so provisioned as usual
                opts.PublishMessage<PubsubShipmentNotice>()
                    .ToPubsubTopic(notifications);
            });

        (await configure().RunJasperFxCommands(["resources", "setup"])).ShouldBe(0);

        (await subscriptionExistsAsync(events)).ShouldBeTrue();
        (await topicExistsAsync(notifications)).ShouldBeTrue();

        // check still covers the externally owned objects, and they all exist
        (await configure().RunJasperFxCommands(["resources", "check"])).ShouldBe(0);

        (await configure().RunJasperFxCommands(["resources", "teardown"])).ShouldBe(0);

        // What this application owns is gone...
        (await subscriptionExistsAsync(events)).ShouldBeFalse();
        (await topicExistsAsync(notifications)).ShouldBeFalse();

        // ...and what it doesn't is still there
        (await topicExistsAsync(events)).ShouldBeTrue();
        (await topicExistsAsync(audit)).ShouldBeTrue();
        (await topicExistsAsync(inbox)).ShouldBeTrue();
        (await subscriptionExistsAsync(inbox)).ShouldBeTrue();
    }

    private static T? findInChain<T>(Exception? ex) where T : Exception
    {
        while (ex != null)
        {
            if (ex is T found) return found;

            if (ex is AggregateException aggregate)
            {
                foreach (var inner in aggregate.InnerExceptions)
                {
                    var match = findInChain<T>(inner);
                    if (match != null) return match;
                }
            }

            ex = ex.InnerException;
        }

        return null;
    }
}

public record PubsubAuditEntryRecorded;
public record PubsubShipmentNotice;
