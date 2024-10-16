using Google.Api.Gax;
using Google.Cloud.PubSub.V1;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using NSubstitute.ExceptionExtensions;
using Shouldly;
using Wolverine.Pubsub.Internal;
using Xunit;

namespace Wolverine.Pubsub.Tests.Internal;

public class PubsubTopicTests {
    private PubsubTransport createTransport() => new("wolverine") {
        PublisherApiClient = Substitute.For<PublisherServiceApiClient>(),
        SubscriberApiClient = Substitute.For<SubscriberServiceApiClient>(),
        EmulatorDetection = EmulatorDetection.EmulatorOnly
    };

    [Fact]
    public void create_uri() {
        var topic = new PubsubTopic("top1", createTransport());

        topic.Uri.ShouldBe(new Uri($"{PubsubTransport.ProtocolName}://top1"));
    }

    [Fact]
    public void endpoint_name_is_topic_name_without_prefix() {
        var topic = new PubsubTopic("top1", createTransport());

        topic.EndpointName.ShouldBe("top1");
    }

    [Fact]
    public async Task initialize_with_no_auto_provision() {
        var transport = createTransport();
        var topic = new PubsubTopic("foo", transport);

        await topic.InitializeAsync(NullLogger.Instance);

        await transport.PublisherApiClient!.DidNotReceive().CreateTopicAsync(Arg.Any<TopicName>());
    }

    [Fact]
    public async Task initialize_with_auto_provision() {
        var transport = createTransport();

        transport.AutoProvision = true;

        var topic = new PubsubTopic("foo", transport);

        transport.PublisherApiClient!.GetTopicAsync(Arg.Is(topic.Name)).Throws<Exception>();

        await topic.InitializeAsync(NullLogger.Instance);

        await transport.PublisherApiClient!.Received().CreateTopicAsync(Arg.Is(topic.Name));
    }
}
