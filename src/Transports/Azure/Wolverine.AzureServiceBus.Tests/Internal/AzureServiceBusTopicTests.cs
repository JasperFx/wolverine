using Azure.Messaging.ServiceBus.Administration;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Shouldly;
using Wolverine.AzureServiceBus.Internal;
using Xunit;

namespace Wolverine.AzureServiceBus.Tests.Internal;

public class AzureServiceBusTopicTests
{
    private readonly ServiceBusAdministrationClient theManagementClient = Substitute.For<ServiceBusAdministrationClient>();
    private readonly AzureServiceBusTransport theTransport = new();

    [Fact]
    public void create_uri()
    {
        var topic = new AzureServiceBusTopic(new AzureServiceBusTransport(), "incoming");
        topic.Uri.ShouldBe(new Uri("asb://topic/incoming"));
    }

    [Fact]
    public void endpoint_name_should_be_topic_name()
    {
        var topic = new AzureServiceBusTopic(new AzureServiceBusTransport(), "incoming");
        topic.EndpointName.ShouldBe("incoming");
    }

    [Fact]
    public async Task initialize_with_no_auto_provision()
    {
        theTransport.AutoProvision = false;

        var endpoint = new AzureServiceBusTopic(theTransport, "foo");

        await endpoint.InitializeAsync(theManagementClient, NullLogger.Instance);

        await theManagementClient.DidNotReceive().CreateTopicAsync(Arg.Any<CreateTopicOptions>());
    }

    [Fact]
    public async Task initialize_with_auto_provision()
    {
        theTransport.AutoProvision = true;

        var endpoint = new AzureServiceBusTopic(theTransport, "foo");

        await endpoint.InitializeAsync(theManagementClient, NullLogger.Instance);

        await theManagementClient.Received().CreateTopicAsync(Arg.Is<CreateTopicOptions>(x => x.Name == "foo"));
    }
}