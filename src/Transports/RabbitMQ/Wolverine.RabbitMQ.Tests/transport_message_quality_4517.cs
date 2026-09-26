using Shouldly;
using Wolverine.RabbitMQ.Internal;
using Xunit;

namespace Wolverine.RabbitMQ.Tests;

/// <summary>
/// GH-4517: the topic Uri guard and all three "you cannot listen to this" refusals threw with no
/// message at all, and the "has not been created yet or is disabled!" family never said which of
/// the three causes applied.
/// </summary>
public class transport_message_quality_4517
{
    [Fact]
    public void topic_uri_with_the_wrong_shape_names_the_expected_form()
    {
        var transport = new RabbitMqTransport();

        var ex = Should.Throw<ArgumentOutOfRangeException>(() =>
            transport.Topics[new Uri("rabbitmq://exchange/colors/red")]);

        ex.Message.ShouldContain("rabbitmq://topic/{exchangeName}/{topicName}");
        ex.Message.ShouldContain("rabbitmq://exchange/colors/red");
    }

    [Fact]
    public async Task listening_to_an_exchange_points_at_a_bound_queue()
    {
        var transport = new RabbitMqTransport();
        var exchange = transport.Exchanges["colors"];

        var ex = await Should.ThrowAsync<NotSupportedException>(async () =>
            await exchange.BuildListenerAsync(null!, null!));

        ex.Message.ShouldContain("colors");
        ex.Message.ShouldContain("publish-only");
        ex.Message.ShouldContain("ListenToRabbitQueue(queueName)");
    }

    [Fact]
    public async Task listening_to_a_routing_key_points_at_a_bound_queue()
    {
        var transport = new RabbitMqTransport();
        var exchange = transport.Exchanges["colors"];
        var routing = new RabbitMqRouting(exchange, "red", transport);

        var ex = await Should.ThrowAsync<NotSupportedException>(async () =>
            await routing.BuildListenerAsync(null!, null!));

        ex.Message.ShouldContain("red");
        ex.Message.ShouldContain("colors");
        ex.Message.ShouldContain("ListenToRabbitQueue(queueName)");
    }

    [Fact]
    public async Task listening_to_a_topic_endpoint_points_at_a_bound_queue()
    {
        var transport = new RabbitMqTransport();
        var topic = transport.Topics[new Uri("rabbitmq://topic/colors/red")];

        var ex = await Should.ThrowAsync<NotSupportedException>(async () =>
            await topic.BuildListenerAsync(null!, null!));

        ex.Message.ShouldContain("red");
        ex.Message.ShouldContain("ListenToRabbitQueue(queueName)");
    }

    [Fact]
    public void the_listening_connection_names_all_three_causes()
    {
        var transport = new RabbitMqTransport();

        var ex = Should.Throw<InvalidOperationException>(() => transport.ListeningConnection);

        ex.Message.ShouldContain("UseRabbitMq()");
        ex.Message.ShouldContain("has not been started");
        // the listening side is what UseSenderConnectionOnly() switches off
        ex.Message.ShouldContain("UseSenderConnectionOnly()");
    }

    [Fact]
    public void the_sending_connection_names_all_three_causes()
    {
        var transport = new RabbitMqTransport();

        var ex = Should.Throw<InvalidOperationException>(() => transport.SendingConnection);

        ex.Message.ShouldContain("UseRabbitMq()");
        ex.Message.ShouldContain("has not been started");
        ex.Message.ShouldContain("UseListenerConnectionOnly()");
    }

    [Fact]
    public async Task creating_a_connection_without_a_factory_says_UseRabbitMq_was_never_called()
    {
        var transport = new RabbitMqTransport();

        var ex = await Should.ThrowAsync<InvalidOperationException>(async () =>
            await transport.CreateConnectionAsync());

        ex.Message.ShouldContain("UseRabbitMq()");
        ex.Message.ShouldContain("ConnectionFactory");
    }
}
