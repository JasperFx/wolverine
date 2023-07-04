using Azure.Messaging.ServiceBus.Administration;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Shouldly;
using Wolverine.AzureServiceBus.Internal;
using Xunit;

namespace Wolverine.AzureServiceBus.Tests.Internal;

public class AzureServiceSubscriptionTests
{
    private readonly ServiceBusAdministrationClient theManagementClient = Substitute.For<ServiceBusAdministrationClient>();
    private readonly AzureServiceBusTransport theTransport = new();

    [Fact]
    public void create_uri()
    {
        var topic = new AzureServiceBusTopic(new AzureServiceBusTransport(), "incoming");
        var subscription = new AzureServiceBusSubscription(new AzureServiceBusTransport(), topic, "sub1");
        subscription.Uri.ShouldBe(new Uri("asb://topic/incoming/sub1"));
    }

    [Fact]
    public void endpoint_name_should_be_subscription_name()
    {
        var topic = new AzureServiceBusTopic(new AzureServiceBusTransport(), "incoming");
        var subscription = new AzureServiceBusSubscription(new AzureServiceBusTransport(), topic, "sub1");

        subscription.EndpointName.ShouldBe("sub1");
    }

    [Fact]
    public async Task initialize_with_no_auto_provision()
    {
        theTransport.AutoProvision = false;
       
        var topic = new AzureServiceBusTopic(theTransport, "foo");
        var subscription = new AzureServiceBusSubscription(theTransport, topic, "bar");

        await subscription.InitializeAsync(theManagementClient, NullLogger.Instance);

        await theManagementClient.DidNotReceive().CreateSubscriptionAsync(Arg.Any<CreateSubscriptionOptions>());
    }

    [Fact]
    public async Task initialize_with_auto_provision()
    {
        theTransport.AutoProvision = true;

        var topic = new AzureServiceBusTopic(theTransport, "foo");
        var subscription = new AzureServiceBusSubscription(theTransport, topic, "bar");

        await subscription.InitializeAsync(theManagementClient, NullLogger.Instance);

        await theManagementClient.Received().CreateSubscriptionAsync(Arg.Is<CreateSubscriptionOptions>(x => x.TopicName == "foo" && x.SubscriptionName == "bar"));
    }
}