using Shouldly;
using Wolverine.AmazonSns.Internal;
using Xunit;

namespace Wolverine.AmazonSns.Tests;

/// <summary>
/// GH-4517: the SNS Uri guard threw with no message at all, and "listening" to a publish-only
/// topic threw a completely empty NotSupportedException.
/// </summary>
public class transport_message_quality_4517
{
    [Fact]
    public void uri_with_the_wrong_scheme_names_the_expected_shape()
    {
        var transport = new AmazonSnsTransport();

        var ex = Should.Throw<ArgumentOutOfRangeException>(() =>
            transport.TryGetEndpoint(new Uri("sn://notifications")));

        ex.Message.ShouldContain("sns://{topicName}");
        ex.Message.ShouldContain("sn://notifications");
    }

    [Fact]
    public async Task listening_to_a_topic_says_to_listen_to_a_subscribed_queue_instead()
    {
        var transport = new AmazonSnsTransport();
        var topic = transport.Topics["notifications"];

        var ex = await Should.ThrowAsync<NotSupportedException>(async () =>
            await topic.BuildListenerAsync(null!, null!));

        ex.Message.ShouldContain("notifications");
        ex.Message.ShouldContain("publish-only");
        ex.Message.ShouldContain("ListenToSqsQueue()");
    }

    [Fact]
    public void not_initialized_message_names_both_causes()
    {
        var message = AmazonSnsTransport.NotInitializedMessage(new Uri("sns://notifications"));

        message.ShouldContain("sns://notifications");
        message.ShouldContain("UseAmazonSnsTransport()");
        message.ShouldContain("has not been started");
    }
}
