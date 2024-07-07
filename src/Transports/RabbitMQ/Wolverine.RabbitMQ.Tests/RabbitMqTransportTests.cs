using JasperFx.Core;
using Shouldly;
using Wolverine.RabbitMQ.Internal;
using Xunit;

namespace Wolverine.RabbitMQ.Tests;

public class RabbitMqTransportTests
{
    private readonly RabbitMqTransport theTransport = new();


    [Fact]
    public void automatic_recovery_is_try_by_default()
    {
        theTransport.ConfigureFactory(f => {});
        theTransport.ConnectionFactory.AutomaticRecoveryEnabled.ShouldBeTrue();
    }

    [Fact]
    public void auto_provision_is_false_by_default()
    {
        theTransport.AutoProvision.ShouldBeFalse();
    }

    [Fact]
    public void find_by_uri_for_exchange()
    {
        var exchange = theTransport.GetOrCreateEndpoint("rabbitmq://exchange/foo".ToUri())
            .ShouldBeOfType<RabbitMqExchange>();

        exchange.ExchangeName.ShouldBe("foo");
    }

    [Fact]
    public void find_by_uri_for_queue()
    {
        var queue = theTransport.GetOrCreateEndpoint("rabbitmq://queue/foo".ToUri())
            .ShouldBeOfType<RabbitMqQueue>();

        queue.QueueName.ShouldBe("foo");
    }

    [Fact]
    public void find_by_topic()
    {
        var endpoint = theTransport.GetOrCreateEndpoint("rabbitmq://topic/color/blue".ToUri());

        var topic = endpoint.ShouldBeOfType<RabbitMqTopicEndpoint>();
        topic.TopicName.ShouldBe("blue");
        topic.ExchangeName.ShouldBe("color");

        topic.Exchange.Name.ShouldBe("color");
        topic.Exchange.ExchangeType.ShouldBe(ExchangeType.Topic);
    }

    [Fact]
    public void default_dead_letter_queue_settings()
    {
        theTransport.DeadLetterQueue.Mode.ShouldBe(DeadLetterQueueMode.Native);
        theTransport.DeadLetterQueue.QueueName.ShouldBe(RabbitMqTransport.DeadLetterQueueName);
        theTransport.DeadLetterQueue.ExchangeName.ShouldBe(RabbitMqTransport.DeadLetterQueueName);
    }

    [Fact]
    public void declare_request_reply_system_queue_is_true_by_default()
    {
        theTransport.DeclareRequestReplySystemQueue.ShouldBeTrue();
    }

    [Fact]
    public void use_sender_connection_only_is_false_by_default()
    {
        theTransport.UseSenderConnectionOnly.ShouldBeFalse();
    }

    [Fact]
    public void use_listener_connection_only_is_false_by_default()
    {
        theTransport.UseListenerConnectionOnly.ShouldBeFalse();
    }
}