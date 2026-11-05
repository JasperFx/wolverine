using Azure;
using Azure.Messaging.ServiceBus.Administration;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Shouldly;
using Wolverine.AzureServiceBus.Internal;
using Xunit;

namespace Wolverine.AzureServiceBus.Tests;

public class externally_owned_topics_5342
{
    private static ServiceBusAdministrationClient clientWhere(bool topicExists)
    {
        var client = Substitute.For<ServiceBusAdministrationClient>();
        client.TopicExistsAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(Response.FromValue(topicExists, Substitute.For<Response>()));
        client.SubscriptionExistsAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(Response.FromValue(false, Substitute.For<Response>()));
        return client;
    }

    [Fact]
    public async Task never_creates_an_externally_owned_topic()
    {
        var topic = new AzureServiceBusTransport().Topics["orders"];
        topic.IsExternallyOwned = true;
        var client = clientWhere(false);

        await topic.SetupAsync(client, NullLogger.Instance);

        await client.DidNotReceiveWithAnyArgs().CreateTopicAsync(default(CreateTopicOptions)!, TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task creates_a_normal_topic()
    {
        var topic = new AzureServiceBusTransport().Topics["orders"];
        var client = clientWhere(false);

        await topic.SetupAsync(client, NullLogger.Instance);

        await client.ReceivedWithAnyArgs(1).CreateTopicAsync(default(CreateTopicOptions)!, TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task subscription_on_missing_external_topic_fails_naming_the_topic()
    {
        var topic = new AzureServiceBusTransport().Topics["orders"];
        topic.IsExternallyOwned = true;
        var subscription = topic.FindOrCreateSubscription("my-sub");

        var ex = await Should.ThrowAsync<InvalidOperationException>(async () =>
            await subscription.SetupAsync(clientWhere(false), NullLogger.Instance));

        ex.Message.ShouldContain("'orders'");
    }

    [Fact]
    public async Task subscription_on_existing_external_topic_is_still_provisioned()
    {
        var topic = new AzureServiceBusTransport().Topics["orders"];
        topic.IsExternallyOwned = true;
        var subscription = topic.FindOrCreateSubscription("my-sub");
        var client = clientWhere(true);

        await subscription.SetupAsync(client, NullLogger.Instance);

        await client.ReceivedWithAnyArgs(1)
            .CreateSubscriptionAsync(default(CreateSubscriptionOptions)!, default(CreateRuleOptions)!, TestContext.Current.CancellationToken);
    }

    [Fact]
    public void listener_configuration_marks_the_topic()
    {
        var topic = new AzureServiceBusTransport().Topics["orders"];
        var subscription = topic.FindOrCreateSubscription("my-sub");

        var config = new AzureServiceBusSubscriptionListenerConfiguration(subscription);
        config.TopicIsExternallyOwned();
        ((Wolverine.Configuration.IDelayedEndpointConfiguration)config).Apply();

        topic.IsExternallyOwned.ShouldBeTrue();
    }
}
