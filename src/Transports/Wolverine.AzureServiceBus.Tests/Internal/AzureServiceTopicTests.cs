using System;
using Shouldly;
using Wolverine.AzureServiceBus.Internal;
using Xunit;

namespace Wolverine.AzureServiceBus.Tests.Internal;

public class AzureServiceTopicTests
{
    [Fact]
    public void create_uri()
    {
        var topic = new AzureServiceBusTopic(new AzureServiceBusTransport(), "incoming");
        topic.Uri.ShouldBe(new Uri("asb://topic/incoming"));
    }
}