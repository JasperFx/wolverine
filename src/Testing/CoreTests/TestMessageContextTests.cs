using JasperFx.Core;
using Wolverine.ComplianceTests.Compliance;
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
    public async Task invoke_a_message_inline()
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

        await theContext.EndpointFor("endpoint1").SendAsync(message1, new DeliveryOptions().WithHeader("a", "1"));

        var envelope = theSpy.AllOutgoing.ShouldHaveEnvelopeForMessageType<Message1>();
        envelope
            .Headers["a"].ShouldBe("1");
        envelope.EndpointName.ShouldBe("endpoint1");
    }

    [Fact]
    public async Task send_to_topic()
    {
        var message1 = new Message1();

        await theContext.BroadcastToTopicAsync("topic1", message1, new DeliveryOptions().WithHeader("a", "1"));

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

        await theContext.EndpointFor(uri).SendAsync(message1, new DeliveryOptions().WithHeader("a", "1"));

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
    public async Task invoke_remotely()
    {
        var message1 = new Message1();
        await theContext.InvokeAsync(message1);

        theSpy.Invoked.ShouldHaveMessageOfType<Message1>();
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

    [Fact]
    public async Task invoke_with_expected_response_no_filter_hit()
    {
        var response = new NumberResponse(11);
        theSpy.WhenInvokedMessageOf<NumberRequest>().RespondWith(response);

        (await theContext.InvokeAsync<NumberResponse>(new NumberRequest(3, 4)))
            .ShouldBeSameAs(response);
    }

    [Fact]
    public async Task invoke_with_expected_response_miss()
    {
        var ex = await Should.ThrowAsync<Exception>(async () =>
        {
            await theContext.InvokeAsync<NumberResponse>(new NumberRequest(3, 4));
        });

        ex.Message.ShouldStartWith("There is no matching expectation for the request message");
    }

    [Fact]
    public async Task invoke_with_expected_response_and_filter_hit()
    {
        var response1 = new NumberResponse(11);
        var response2 = new NumberResponse(12);
        theSpy.WhenInvokedMessageOf<NumberRequest>(x => x.X == 3).RespondWith(response1);
        theSpy.WhenInvokedMessageOf<NumberRequest>(x => x.X == 5).RespondWith(response2);

        (await theContext.InvokeAsync<NumberResponse>(new NumberRequest(3, 4)))
            .ShouldBeSameAs(response1);

        (await theContext.InvokeAsync<NumberResponse>(new NumberRequest(5, 4)))
            .ShouldBeSameAs(response2);
    }

    [Fact]
    public async Task invoke_with_expected_response_and_filter_miss()
    {
        var response1 = new NumberResponse(11);
        theSpy.WhenInvokedMessageOf<NumberRequest>(x => x.X == 100).RespondWith(response1);

        var ex = await Should.ThrowAsync<Exception>(async () =>
        {
            // This is a miss
            await theContext.InvokeAsync<NumberResponse>(new NumberRequest(3, 4));
        });

        ex.Message.ShouldStartWith("There is no matching expectation for the request message");
    }

    [Fact]
    public async Task invoke_with_expected_response_no_filter_hit_to_endpoint_by_uri()
    {
        var response1 = new NumberResponse(11);
        var response2 = new NumberResponse(12);
        var destination1 = new Uri("stub://one");
        theSpy.WhenInvokedMessageOf<NumberRequest>(destination:destination1).RespondWith(response1);

        var destination2 = new Uri("stub://two");
        theSpy.WhenInvokedMessageOf<NumberRequest>(destination:destination2).RespondWith(response2);

        (await theContext.EndpointFor(destination1).InvokeAsync<NumberResponse>(new NumberRequest(4, 5))).ShouldBeSameAs(response1);

        (await theContext.EndpointFor(destination2).InvokeAsync<NumberResponse>(new NumberRequest(4, 5))).ShouldBeSameAs(response2);
    }

    [Fact]
    public async Task invoke_with_expected_response_miss_to_endpoint_by_uri()
    {
        var response1 = new NumberResponse(11);
        var destination1 = new Uri("stub://one");
        theSpy.WhenInvokedMessageOf<NumberRequest>(destination:destination1).RespondWith(response1);

        var ex = await Should.ThrowAsync<Exception>(async () =>
        {
            // This is a miss
            await theContext.EndpointFor(new Uri("stub://wrong")).InvokeAsync<NumberResponse>(new NumberRequest(3, 4));
        });

        ex.Message.ShouldStartWith("There is no matching expectation for the request message");
    }

    [Fact]
    public async Task invoke_with_expected_response_and_filter_hit_to_endpoint_by_uri()
    {
        var response1 = new NumberResponse(11);
        var destination1 = new Uri("stub://one");
        theSpy.WhenInvokedMessageOf<NumberRequest>(x => x.X == 4,destination:destination1).RespondWith(response1);

        (await theContext.EndpointFor(destination1).InvokeAsync<NumberResponse>(new NumberRequest(4, 5))).ShouldBeSameAs(response1);
    }

    [Fact]
    public async Task invoke_with_expected_response_and_filter_miss_to_endpoint_by_uri()
    {
        var response1 = new NumberResponse(11);
        var destination1 = new Uri("stub://one");
        theSpy.WhenInvokedMessageOf<NumberRequest>(x => x.X == 4,destination:destination1).RespondWith(response1);

        var ex = await Should.ThrowAsync<Exception>(async () =>
        {
            // This is a miss
            await theContext.EndpointFor(new Uri("stub://wrong")).InvokeAsync<NumberResponse>(new NumberRequest(4, 4));
        });

        ex.Message.ShouldStartWith("There is no matching expectation for the request message");

    }

    [Fact]
    public async Task invoke_with_expected_response_no_filter_hit_to_endpoint_by_name()
    {
        var response1 = new NumberResponse(11);
        var response2 = new NumberResponse(12);
        theSpy.WhenInvokedMessageOf<NumberRequest>(endpointName:"one").RespondWith(response1);

        theSpy.WhenInvokedMessageOf<NumberRequest>(endpointName:"two").RespondWith(response2);

        (await theContext.EndpointFor("one").InvokeAsync<NumberResponse>(new NumberRequest(4, 5))).ShouldBeSameAs(response1);

        (await theContext.EndpointFor("two").InvokeAsync<NumberResponse>(new NumberRequest(4, 5))).ShouldBeSameAs(response2);
    }

    [Fact]
    public async Task invoke_with_expected_response_miss_to_endpoint_by_name()
    {
        var response1 = new NumberResponse(11);
        theSpy.WhenInvokedMessageOf<NumberRequest>(endpointName:"one").RespondWith(response1);

        var ex = await Should.ThrowAsync<Exception>(async () =>
        {
            // This is a miss
            await theContext.EndpointFor("wrong").InvokeAsync<NumberResponse>(new NumberRequest(3, 4));
        });

        ex.Message.ShouldStartWith("There is no matching expectation for the request message");
    }

    [Fact]
    public async Task invoke_with_expected_response_and_filter_hit_to_endpoint_by_name()
    {
        var response1 = new NumberResponse(11);
        theSpy.WhenInvokedMessageOf<NumberRequest>(x => x.X == 4,endpointName:"one").RespondWith(response1);

        (await theContext.EndpointFor("one").InvokeAsync<NumberResponse>(new NumberRequest(4, 5))).ShouldBeSameAs(response1);
    }

    [Fact]
    public async Task invoke_with_expected_response_and_filter_miss_to_endpoint_by_name()
    {
        var response1 = new NumberResponse(11);
        theSpy.WhenInvokedMessageOf<NumberRequest>(x => x.X == 4,endpointName:"one").RespondWith(response1);

        var ex = await Should.ThrowAsync<Exception>(async () =>
        {
            // This is a miss
            await theContext.EndpointFor("wrong").InvokeAsync<NumberResponse>(new NumberRequest(4, 4));
        });

        ex.Message.ShouldStartWith("There is no matching expectation for the request message");
    }

    [Fact]
    public async Task invoke_acknowledgement_with_delivery_options_to_endpoint_by_uri()
    {
        var uri = "something://one".ToUri();
        var message1 = new Message1();

        await theContext.EndpointFor(uri).InvokeAsync(message1, new DeliveryOptions().WithHeader("ack-test", "value"));

        var envelope = theSpy.Sent.ShouldHaveEnvelopeForMessageType<Message1>();
        envelope.Destination.ShouldBe(uri);
        envelope.Headers["ack-test"].ShouldBe("value");
    }

    [Fact]
    public async Task invoke_acknowledgement_with_delivery_options_to_endpoint_by_name()
    {
        var message1 = new Message1();

        await theContext.EndpointFor("endpoint1").InvokeAsync(message1, new DeliveryOptions().WithHeader("ack-name-test", "value"));

        var envelope = theSpy.Sent.ShouldHaveEnvelopeForMessageType<Message1>();
        envelope.EndpointName.ShouldBe("endpoint1");
        envelope.Headers["ack-name-test"].ShouldBe("value");
    }

    [Fact]
    public async Task invoke_with_expected_response_and_delivery_options_no_filter_hit()
    {
        var response = new NumberResponse(11);
        theSpy.WhenInvokedMessageOf<NumberRequest>().RespondWith(response);

        var result = await theContext.InvokeAsync<NumberResponse>(
            new NumberRequest(3, 4),
            new DeliveryOptions().WithHeader("custom", "value"));

        result.ShouldBeSameAs(response);

        var envelope = theSpy.Invoked.OfType<Envelope>().Last();
        envelope.Headers["custom"].ShouldBe("value");
    }

    [Fact]
    public async Task invoke_with_expected_response_and_delivery_options_and_filter_hit()
    {
        var response1 = new NumberResponse(11);
        var response2 = new NumberResponse(12);
        theSpy.WhenInvokedMessageOf<NumberRequest>(x => x.X == 3).RespondWith(response1);
        theSpy.WhenInvokedMessageOf<NumberRequest>(x => x.X == 5).RespondWith(response2);

        var result1 = await theContext.InvokeAsync<NumberResponse>(
            new NumberRequest(3, 4),
            new DeliveryOptions().WithHeader("test", "one"));

        result1.ShouldBeSameAs(response1);

        var result2 = await theContext.InvokeAsync<NumberResponse>(
            new NumberRequest(5, 4),
            new DeliveryOptions().WithHeader("test", "two"));

        result2.ShouldBeSameAs(response2);
    }

    [Fact]
    public async Task invoke_with_expected_response_and_delivery_options_and_filter_miss()
    {
        var response1 = new NumberResponse(11);
        theSpy.WhenInvokedMessageOf<NumberRequest>(x => x.X == 100).RespondWith(response1);

        var ex = await Should.ThrowAsync<Exception>(async () =>
        {
            await theContext.InvokeAsync<NumberResponse>(
                new NumberRequest(3, 4),
                new DeliveryOptions().WithHeader("test", "value"));
        });

        ex.Message.ShouldStartWith("There is no matching expectation for the request message");
    }

    [Fact]
    public async Task invoke_with_expected_response_and_delivery_options_to_endpoint_by_uri()
    {
        var response = new NumberResponse(11);
        var destination = new Uri("stub://one");
        theSpy.WhenInvokedMessageOf<NumberRequest>(destination: destination).RespondWith(response);

        var result = await theContext.EndpointFor(destination)
            .InvokeAsync<NumberResponse>(
                new NumberRequest(4, 5),
                new DeliveryOptions().WithHeader("uri-test", "value"));

        result.ShouldBeSameAs(response);

        var envelope = theSpy.Invoked.OfType<Envelope>().Last();
        envelope.Headers["uri-test"].ShouldBe("value");
        envelope.Destination.ShouldBe(destination);
    }

    [Fact]
    public async Task invoke_with_expected_response_and_delivery_options_and_filter_to_endpoint_by_uri()
    {
        var response = new NumberResponse(11);
        var destination = new Uri("stub://one");
        theSpy.WhenInvokedMessageOf<NumberRequest>(x => x.X == 4, destination: destination).RespondWith(response);

        var result = await theContext.EndpointFor(destination)
            .InvokeAsync<NumberResponse>(
                new NumberRequest(4, 5),
                new DeliveryOptions().WithHeader("filter-uri-test", "value"));

        result.ShouldBeSameAs(response);
    }

    [Fact]
    public async Task invoke_with_expected_response_and_delivery_options_to_endpoint_by_name()
    {
        var response = new NumberResponse(11);
        theSpy.WhenInvokedMessageOf<NumberRequest>(endpointName: "one").RespondWith(response);

        var result = await theContext.EndpointFor("one")
            .InvokeAsync<NumberResponse>(
                new NumberRequest(4, 5),
                new DeliveryOptions().WithHeader("name-test", "value"));

        result.ShouldBeSameAs(response);

        var envelope = theSpy.Invoked.OfType<Envelope>().Last();
        envelope.Headers["name-test"].ShouldBe("value");
        envelope.EndpointName.ShouldBe("one");
    }

    [Fact]
    public async Task invoke_with_expected_response_and_delivery_options_and_filter_to_endpoint_by_name()
    {
        var response = new NumberResponse(11);
        theSpy.WhenInvokedMessageOf<NumberRequest>(x => x.X == 4, endpointName: "one").RespondWith(response);

        var result = await theContext.EndpointFor("one")
            .InvokeAsync<NumberResponse>(
                new NumberRequest(4, 5),
                new DeliveryOptions().WithHeader("filter-name-test", "value"));

        result.ShouldBeSameAs(response);
    }

    public static async Task set_up_invoke_expectations()
    {
        #region sample_using_invoke_with_expected_response_with_test_message_context

        var spy = new TestMessageContext();
        var context = (IMessageContext)spy;

        // Set up an expected response for a message
        spy.WhenInvokedMessageOf<NumberRequest>()
            .RespondWith(new NumberResponse(12));

        // Used for:
        var response1 = await context.InvokeAsync<NumberResponse>(new NumberRequest(4, 5));

        // Set up an expected response with a matching filter
        spy.WhenInvokedMessageOf<NumberRequest>(x => x.X == 4)
            .RespondWith(new NumberResponse(12));

        // Set up an expected response for a message to an explicit destination Uri
        spy.WhenInvokedMessageOf<NumberRequest>(destination:new Uri("rabbitmq://queue/incoming"))
            .RespondWith(new NumberResponse(12));

        // Used to set up:
        var response2 = await context.EndpointFor(new Uri("rabbitmq://queue/incoming"))
            .InvokeAsync<NumberResponse>(new NumberRequest(5, 6));

        // Set up an expected response for a message to a named endpoint
        spy.WhenInvokedMessageOf<NumberRequest>(endpointName:"incoming")
            .RespondWith(new NumberResponse(12));

        // Used to set up:
        var response3 = await context.EndpointFor("incoming")
            .InvokeAsync<NumberResponse>(new NumberRequest(5, 6));

        #endregion
    }
}

public record NumberRequest(int X, int Y);
public record NumberResponse(int Value);