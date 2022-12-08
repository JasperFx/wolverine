using System;
using System.Threading.Tasks;
using JasperFx.Core;
using TestMessages;
using Wolverine.Util;
using Xunit;

namespace CoreTests;

public class TestMessageContextTests
{
    private readonly TestMessageContext theSpy = new(new Message1());

    private IMessageContext theContext => theSpy;

    [Fact]
    public void basic_members()
    {
        theSpy.Envelope.ShouldNotBeNull();
        theSpy.Envelope.Message.ShouldBeOfType<Message1>();
        theSpy.CorrelationId.ShouldNotBeEmpty();
    }

    [Fact]
    public async Task invoke_locally()
    {
        var message = new Message2();
        await theContext.InvokeAsync(message);

        theSpy.Invoked.ShouldHaveMessageOfType<Message2>()
            .ShouldBeSameAs(message);
    }

    [Fact]
    public async Task schedule_by_execution_time()
    {
        var message = new Message2();
        var time = new DateTimeOffset(DateTime.Today);

        await theContext.ScheduleAsync(message, time);

        theSpy.ScheduledMessages().FindForMessageType<Message2>()
            .ScheduledTime.ShouldBe(time);
    }

    [Fact]
    public async Task schedule_by_delay_time()
    {
        var message = new Message2();

        await theContext.ScheduleAsync(message, 1.Days());

        theSpy.ScheduledMessages().FindForMessageType<Message2>()
            .ScheduledTime.ShouldNotBeNull();
    }

    [Fact]
    public async Task send_with_delivery_options()
    {
        var message1 = new Message1();

        await theContext.SendAsync(message1, new DeliveryOptions().WithHeader("a", "1"));

        theSpy.Sent.ShouldHaveEnvelopeForMessageType<Message1>()
            .Headers["a"].ShouldBe("1");
    }


    [Fact]
    public async Task publish_with_delivery_options()
    {
        var message1 = new Message1();

        await theContext.PublishAsync(message1, new DeliveryOptions().WithHeader("a", "1"));

        theSpy.Published.ShouldHaveEnvelopeForMessageType<Message1>()
            .Headers["a"].ShouldBe("1");
    }

    [Fact]
    public async Task send_to_endpoint()
    {
        var message1 = new Message1();

        await theContext.SendToEndpointAsync("endpoint1", message1, new DeliveryOptions().WithHeader("a", "1"));

        var envelope = theSpy.AllOutgoing.ShouldHaveEnvelopeForMessageType<Message1>();
        envelope
            .Headers["a"].ShouldBe("1");
        envelope.EndpointName.ShouldBe("endpoint1");
    }

    [Fact]
    public async Task send_to_topic()
    {
        var message1 = new Message1();

        await theContext.SendToTopicAsync("topic1", message1, new DeliveryOptions().WithHeader("a", "1"));

        var envelope = theSpy.AllOutgoing.ShouldHaveEnvelopeForMessageType<Message1>();
        envelope
            .Headers["a"].ShouldBe("1");
        envelope.TopicName.ShouldBe("topic1");
    }

    [Fact]
    public async Task send_directly_to_destination()
    {
        var uri = "something://one".ToUri();
        var message1 = new Message1();

        await theContext.SendAsync(uri, message1, new DeliveryOptions().WithHeader("a", "1"));

        var envelope = theSpy.AllOutgoing.ShouldHaveEnvelopeForMessageType<Message1>();
        envelope
            .Headers["a"].ShouldBe("1");
        envelope.Destination.ShouldBe(uri);
    }

    [Fact]
    public async Task respond_to_sender()
    {
        var message1 = new Message1();

        await theContext.RespondToSenderAsync(message1);

        theSpy.ResponsesToSender.ShouldHaveMessageOfType<Message1>();
    }

    [Fact]
    public async Task send_and_await()
    {
        var message1 = new Message1();
        await theContext.SendAndWaitAsync(message1);

        theSpy.Sent.ShouldHaveMessageOfType<Message1>();
    }

    [Fact]
    public async Task send_and_await_to_destination()
    {
        var uri = "something://one".ToUri();
        var message1 = new Message1();

        await theContext.EndpointFor(uri).InvokeAsync(message1);

        var env = theSpy.Sent.ShouldHaveEnvelopeForMessageType<Message1>();
        env.Destination.ShouldBe(uri);
    }

    [Fact]
    public async Task send_and_await_to_specific_endpoint()
    {
        var message1 = new Message1();

        await theContext.EndpointFor("endpoint1").InvokeAsync(message1);

        var env = theSpy.Sent.ShouldHaveEnvelopeForMessageType<Message1>();
        env.EndpointName.ShouldBe("endpoint1");
    }
}