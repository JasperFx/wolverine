using Shouldly;
using Xunit;

namespace Wolverine.Pubsub.Tests;

public class PubsubTransportTests
{
    [Fact]
    public void find_topic_by_uri()
    {
        var transport = new PubsubTransport("wolverine");
        var topic = transport.GetOrCreateEndpoint(new Uri($"{PubsubTransport.ProtocolName}://wolverine/one"))
            .ShouldBeOfType<PubsubEndpoint>();

        topic.Server.Topic.Name.TopicId.ShouldBe("one");
        topic.Server.Subscription.Name.SubscriptionId.ShouldBe("one");
    }

    [Fact]
    public void response_subscriptions_are_disabled_by_default()
    {
        var transport = new PubsubTransport("wolverine");

        transport.SystemEndpointsEnabled.ShouldBeFalse();
    }

    [Fact]
    public void return_all_endpoints_gets_dead_letter_subscription_too()
    {
        var transport = new PubsubTransport("wolverine");

        transport.DeadLetter.Enabled = true;

        var one = transport.Topics["one"];
        var two = transport.Topics["two"];
        var three = transport.Topics["three"];

        one.DeadLetterName = null;
        two.DeadLetterName = "two-dead-letter";
        two.IsListener = true;
        three.IsListener = true;

        var endpoints = transport.Endpoints().OfType<PubsubEndpoint>().ToArray();

        endpoints.ShouldContain(x => x.EndpointName == "one");
        endpoints.ShouldContain(x => x.EndpointName == "two");
        endpoints.ShouldContain(x => x.EndpointName == "three");

        endpoints.ShouldContain(x => x.EndpointName == PubsubTransport.DeadLetterName);
        endpoints.ShouldContain(x => x.EndpointName == "two-dead-letter");
    }

    [Fact]
    public void findEndpointByUri_should_correctly_find_by_queuename()
    {
        var queueNameInPascalCase = "TestQueue";
        var queueNameLowerCase = "testqueue";

        var transport = new PubsubTransport("wolverine");
        var abc = transport.Topics[queueNameInPascalCase];
        var xzy = transport.Topics[queueNameLowerCase];

        var result =
            transport.GetOrCreateEndpoint(
                new Uri($"{PubsubTransport.ProtocolName}://{transport.ProjectId}/{queueNameInPascalCase}"));

        transport.Topics.Count.ShouldBe(2);

        result.EndpointName.ShouldBe(queueNameInPascalCase);
    }
}