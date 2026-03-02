using NSubstitute;
using Wolverine.Shims.NServiceBus;
using Xunit;

namespace CoreTests.Shims;

public class nservicebus_options_tests
{
    [Fact]
    public void send_options_sets_headers()
    {
        var options = new SendOptions();
        options.SetHeader("key1", "value1");
        options.SetHeader("key2", "value2");

        var delivery = options.ToDeliveryOptions();

        delivery.Headers["key1"].ShouldBe("value1");
        delivery.Headers["key2"].ShouldBe("value2");
    }

    [Fact]
    public void send_options_sets_destination()
    {
        var options = new SendOptions();
        options.SetDestination("my-endpoint");

        options.Destination.ShouldBe("my-endpoint");
    }

    [Fact]
    public void send_options_delay_delivery()
    {
        var options = new SendOptions();
        options.DelayDeliveryWith(TimeSpan.FromMinutes(5));

        var delivery = options.ToDeliveryOptions();

        delivery.ScheduleDelay.ShouldBe(TimeSpan.FromMinutes(5));
        delivery.ScheduledTime.ShouldBeNull();
    }

    [Fact]
    public void send_options_do_not_deliver_before()
    {
        var scheduledTime = DateTimeOffset.UtcNow.AddHours(1);
        var options = new SendOptions();
        options.DoNotDeliverBefore(scheduledTime);

        var delivery = options.ToDeliveryOptions();

        delivery.ScheduledTime.ShouldBe(scheduledTime);
        delivery.ScheduleDelay.ShouldBeNull();
    }

    [Fact]
    public void send_options_delay_clears_scheduled_time()
    {
        var options = new SendOptions();
        options.DoNotDeliverBefore(DateTimeOffset.UtcNow.AddHours(1));
        options.DelayDeliveryWith(TimeSpan.FromMinutes(5));

        var delivery = options.ToDeliveryOptions();

        delivery.ScheduleDelay.ShouldBe(TimeSpan.FromMinutes(5));
        delivery.ScheduledTime.ShouldBeNull();
    }

    [Fact]
    public void publish_options_sets_headers()
    {
        var options = new PublishOptions();
        options.SetHeader("event-type", "created");

        var delivery = options.ToDeliveryOptions();

        delivery.Headers["event-type"].ShouldBe("created");
    }

    [Fact]
    public void reply_options_sets_headers()
    {
        var options = new ReplyOptions();
        options.SetHeader("reply-key", "reply-value");

        var delivery = options.ToDeliveryOptions();

        delivery.Headers["reply-key"].ShouldBe("reply-value");
    }

    [Fact]
    public void get_headers_returns_empty_when_none_set()
    {
        var options = new SendOptions();
        options.GetHeaders().Count.ShouldBe(0);
    }
}

public class wolverine_message_session_tests
{
    private readonly IMessageBus _bus;
    private readonly WolverineMessageSession _session;

    public wolverine_message_session_tests()
    {
        _bus = Substitute.For<IMessageBus>();
        _session = new WolverineMessageSession(_bus);
    }

    [Fact]
    public async Task send_delegates_to_bus_send_async()
    {
        var message = new TestNsbCommand("hello");

        await _session.Send(message);

        await _bus.Received(1).SendAsync(message, null);
    }

    [Fact]
    public async Task send_with_options_delegates_with_delivery_options()
    {
        var message = new TestNsbCommand("hello");
        var options = new SendOptions();
        options.SetHeader("key", "value");

        await _session.Send(message, options);

        await _bus.Received(1).SendAsync(message,
            Arg.Is<DeliveryOptions>(d => d.Headers["key"] == "value"));
    }

    [Fact]
    public async Task send_with_destination_uses_endpoint()
    {
        var message = new TestNsbCommand("hello");
        var options = new SendOptions();
        options.SetDestination("remote-endpoint");

        var endpoint = Substitute.For<IDestinationEndpoint>();
        _bus.EndpointFor("remote-endpoint").Returns(endpoint);

        await _session.Send(message, options);

        await endpoint.Received(1).SendAsync(message, Arg.Any<DeliveryOptions?>());
        await _bus.DidNotReceive().SendAsync(Arg.Any<object>(), Arg.Any<DeliveryOptions?>());
    }

    [Fact]
    public async Task publish_delegates_to_bus_publish_async()
    {
        var message = new TestNsbEvent("created");

        await _session.Publish(message);

        await _bus.Received(1).PublishAsync(message, null);
    }

    [Fact]
    public async Task publish_with_options_delegates_with_delivery_options()
    {
        var message = new TestNsbEvent("created");
        var options = new PublishOptions();
        options.SetHeader("event-source", "test");

        await _session.Publish(message, options);

        await _bus.Received(1).PublishAsync(message,
            Arg.Is<DeliveryOptions>(d => d.Headers["event-source"] == "test"));
    }
}

public class wolverine_endpoint_instance_tests
{
    private readonly IMessageBus _bus;
    private readonly Microsoft.Extensions.Hosting.IHost _host;
    private readonly WolverineEndpointInstance _instance;

    public wolverine_endpoint_instance_tests()
    {
        _bus = Substitute.For<IMessageBus>();
        _host = Substitute.For<Microsoft.Extensions.Hosting.IHost>();
        _instance = new WolverineEndpointInstance(_bus, _host);
    }

    [Fact]
    public async Task send_delegates_to_bus()
    {
        var message = new TestNsbCommand("hello");

        await _instance.Send(message);

        await _bus.Received(1).SendAsync(message, null);
    }

    [Fact]
    public async Task publish_delegates_to_bus()
    {
        var message = new TestNsbEvent("created");

        await _instance.Publish(message);

        await _bus.Received(1).PublishAsync(message, null);
    }

    [Fact]
    public async Task stop_delegates_to_host()
    {
        await _instance.Stop();

        await _host.Received(1).StopAsync(Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task send_with_destination_uses_endpoint()
    {
        var message = new TestNsbCommand("hello");
        var options = new SendOptions();
        options.SetDestination("remote");

        var endpoint = Substitute.For<IDestinationEndpoint>();
        _bus.EndpointFor("remote").Returns(endpoint);

        await _instance.Send(message, options);

        await endpoint.Received(1).SendAsync(message, Arg.Any<DeliveryOptions?>());
    }
}

public class wolverine_message_handler_context_tests
{
    private readonly IMessageContext _context;
    private readonly WolverineMessageHandlerContext _handlerContext;

    public wolverine_message_handler_context_tests()
    {
        _context = Substitute.For<IMessageContext>();
        _handlerContext = new WolverineMessageHandlerContext(_context);
    }

    [Fact]
    public void message_id_returns_envelope_id()
    {
        var envelope = new Envelope { Id = Guid.Parse("12345678-1234-1234-1234-123456789012") };
        _context.Envelope.Returns(envelope);

        _handlerContext.MessageId.ShouldBe("12345678-1234-1234-1234-123456789012");
    }

    [Fact]
    public void message_id_returns_empty_when_no_envelope()
    {
        _context.Envelope.Returns((Envelope?)null);

        _handlerContext.MessageId.ShouldBe(string.Empty);
    }

    [Fact]
    public void reply_to_address_returns_envelope_reply_uri()
    {
        var envelope = new Envelope { ReplyUri = new Uri("tcp://localhost:1234") };
        _context.Envelope.Returns(envelope);

        _handlerContext.ReplyToAddress.ShouldStartWith("tcp://localhost:1234");
    }

    [Fact]
    public void reply_to_address_returns_null_when_no_reply_uri()
    {
        var envelope = new Envelope();
        _context.Envelope.Returns(envelope);

        _handlerContext.ReplyToAddress.ShouldBeNull();
    }

    [Fact]
    public void message_headers_returns_envelope_headers()
    {
        var envelope = new Envelope();
        envelope.Headers["key1"] = "value1";
        _context.Envelope.Returns(envelope);

        _handlerContext.MessageHeaders["key1"].ShouldBe("value1");
    }

    [Fact]
    public void message_headers_returns_empty_when_no_envelope()
    {
        _context.Envelope.Returns((Envelope?)null);

        _handlerContext.MessageHeaders.Count.ShouldBe(0);
    }

    [Fact]
    public void correlation_id_delegates_to_context()
    {
        _context.CorrelationId.Returns("my-correlation-id");

        _handlerContext.CorrelationId.ShouldBe("my-correlation-id");
    }

    [Fact]
    public async Task send_delegates_to_context_send_async()
    {
        var message = new TestNsbCommand("hello");

        await _handlerContext.Send(message);

        await _context.Received(1).SendAsync(message, null);
    }

    [Fact]
    public async Task send_with_destination_uses_endpoint()
    {
        var message = new TestNsbCommand("hello");
        var options = new SendOptions();
        options.SetDestination("remote");

        var endpoint = Substitute.For<IDestinationEndpoint>();
        _context.EndpointFor("remote").Returns(endpoint);

        await _handlerContext.Send(message, options);

        await endpoint.Received(1).SendAsync(message, Arg.Any<DeliveryOptions?>());
    }

    [Fact]
    public async Task publish_delegates_to_context_publish_async()
    {
        var message = new TestNsbEvent("created");

        await _handlerContext.Publish(message);

        await _context.Received(1).PublishAsync(message, null);
    }

    [Fact]
    public async Task reply_delegates_to_respond_to_sender()
    {
        var message = new TestNsbResponse("ok");

        await _handlerContext.Reply(message);

        await _context.Received(1).RespondToSenderAsync(message);
    }

    [Fact]
    public async Task forward_current_message_sends_to_destination()
    {
        var originalMessage = new TestNsbCommand("forward-me");
        var envelope = new Envelope(originalMessage);
        _context.Envelope.Returns(envelope);

        var endpoint = Substitute.For<IDestinationEndpoint>();
        _context.EndpointFor("other-endpoint").Returns(endpoint);

        await _handlerContext.ForwardCurrentMessageTo("other-endpoint");

        await endpoint.Received(1).SendAsync(originalMessage, Arg.Any<DeliveryOptions?>());
    }

    [Fact]
    public async Task forward_current_message_throws_when_no_envelope()
    {
        _context.Envelope.Returns((Envelope?)null);

        await Should.ThrowAsync<InvalidOperationException>(() =>
            _handlerContext.ForwardCurrentMessageTo("other-endpoint"));
    }
}

public class wolverine_uniform_session_tests
{
    private readonly IMessageBus _bus;
    private readonly WolverineUniformSession _session;

    public wolverine_uniform_session_tests()
    {
        _bus = Substitute.For<IMessageBus>();
        _session = new WolverineUniformSession(_bus);
    }

    [Fact]
    public async Task send_delegates_to_bus()
    {
        var message = new TestNsbCommand("hello");

        await _session.Send(message);

        await _bus.Received(1).SendAsync(message, null);
    }

    [Fact]
    public async Task publish_delegates_to_bus()
    {
        var message = new TestNsbEvent("created");

        await _session.Publish(message);

        await _bus.Received(1).PublishAsync(message, null);
    }

    [Fact]
    public async Task send_with_destination_uses_endpoint()
    {
        var message = new TestNsbCommand("hello");
        var options = new SendOptions();
        options.SetDestination("target");

        var endpoint = Substitute.For<IDestinationEndpoint>();
        _bus.EndpointFor("target").Returns(endpoint);

        await _session.Send(message, options);

        await endpoint.Received(1).SendAsync(message, Arg.Any<DeliveryOptions?>());
    }
}

public class wolverine_transactional_session_tests
{
    private readonly IMessageBus _bus;
    private readonly WolverineTransactionalSession _session;

    public wolverine_transactional_session_tests()
    {
        _bus = Substitute.For<IMessageBus>();
        _session = new WolverineTransactionalSession(_bus);
    }

    [Fact]
    public async Task send_delegates_to_bus()
    {
        var message = new TestNsbCommand("hello");

        await _session.Send(message);

        await _bus.Received(1).SendAsync(message, null);
    }

    [Fact]
    public async Task publish_delegates_to_bus()
    {
        var message = new TestNsbEvent("created");

        await _session.Publish(message);

        await _bus.Received(1).PublishAsync(message, null);
    }

    [Fact]
    public async Task open_throws_not_supported()
    {
#pragma warning disable CS0618 // Obsolete
        await Should.ThrowAsync<NotSupportedException>(() => _session.Open());
#pragma warning restore CS0618
    }

    [Fact]
    public async Task commit_throws_not_supported()
    {
#pragma warning disable CS0618 // Obsolete
        await Should.ThrowAsync<NotSupportedException>(() => _session.Commit());
#pragma warning restore CS0618
    }
}

// Test message types
public record TestNsbCommand(string Data);
public record TestNsbEvent(string Data);
public record TestNsbResponse(string Data);
