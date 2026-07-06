using Google.Api.Gax;
using Google.Cloud.PubSub.V1;
using NSubstitute;
using Shouldly;
using Xunit;

namespace Wolverine.Pubsub.Tests;

public class broker_role_tests
{
    [Fact]
    public void pubsub_endpoint_broker_role_is_pubsub()
    {
        var transport = new PubsubTransport("wolverine")
        {
            PublisherApiClient = Substitute.For<PublisherServiceApiClient>(),
            SubscriberApiClient = Substitute.For<SubscriberServiceApiClient>(),
            EmulatorDetection = EmulatorDetection.EmulatorOnly
        };

        new PubsubEndpoint("foo", transport).BrokerRole.ShouldBe("pubsub");
    }
}
