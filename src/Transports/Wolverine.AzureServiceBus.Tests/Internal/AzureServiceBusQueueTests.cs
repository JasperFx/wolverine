using System;
using Shouldly;
using Wolverine.AzureServiceBus.Internal;
using Xunit;

namespace Wolverine.AzureServiceBus.Tests.Internal;

public class AzureServiceBusQueueTests
{
    [Fact]
    public void create_uri()
    {
        var queue = new AzureServiceBusQueue(new AzureServiceBusTransport(), "incoming");
        queue.Uri.ShouldBe(new Uri("asb://queue/incoming"));
    }

    [Fact]
    public void endpoint_name_is_queue_name()
    {
        var queue = new AzureServiceBusQueue(new AzureServiceBusTransport(), "incoming");
        queue.EndpointName.ShouldBe("incoming");
    }
}