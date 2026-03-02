using NSubstitute;
using Wolverine.Shims.MassTransit;
using Xunit;

namespace CoreTests.Shims;

public class wolverine_consume_context_tests
{
    private readonly IMessageContext _context;

    public wolverine_consume_context_tests()
    {
        _context = Substitute.For<IMessageContext>();
    }

    [Fact]
    public void message_returns_the_message()
    {
        var message = new TestMtCommand("hello");
        var consumeContext = new WolverineConsumeContext<TestMtCommand>(_context, message);

        consumeContext.Message.ShouldBe(message);
    }

    [Fact]
    public void message_id_returns_envelope_id()
    {
        var envelope = new Envelope { Id = Guid.Parse("aaaaaaaa-bbbb-cccc-dddd-eeeeeeeeeeee") };
        _context.Envelope.Returns(envelope);

        var consumeContext = new WolverineConsumeContext<TestMtCommand>(_context, new TestMtCommand("test"));

        consumeContext.MessageId.ShouldBe(Guid.Parse("aaaaaaaa-bbbb-cccc-dddd-eeeeeeeeeeee"));
    }

    [Fact]
    public void message_id_returns_null_when_no_envelope()
    {
        _context.Envelope.Returns((Envelope?)null);

        var consumeContext = new WolverineConsumeContext<TestMtCommand>(_context, new TestMtCommand("test"));

        consumeContext.MessageId.ShouldBeNull();
    }

    [Fact]
    public void correlation_id_delegates_to_context()
    {
        _context.CorrelationId.Returns("my-correlation");

        var consumeContext = new WolverineConsumeContext<TestMtCommand>(_context, new TestMtCommand("test"));

        consumeContext.CorrelationId.ShouldBe("my-correlation");
    }

    [Fact]
    public void conversation_id_returns_envelope_conversation_id()
    {
        var conversationId = Guid.NewGuid();
        var envelope = new Envelope { ConversationId = conversationId };
        _context.Envelope.Returns(envelope);

        var consumeContext = new WolverineConsumeContext<TestMtCommand>(_context, new TestMtCommand("test"));

        consumeContext.ConversationId.ShouldBe(conversationId);
    }

    [Fact]
    public void headers_returns_envelope_headers()
    {
        var envelope = new Envelope();
        envelope.Headers["key1"] = "value1";
        _context.Envelope.Returns(envelope);

        var consumeContext = new WolverineConsumeContext<TestMtCommand>(_context, new TestMtCommand("test"));

        consumeContext.Headers["key1"].ShouldBe("value1");
    }

    [Fact]
    public void headers_returns_empty_when_no_envelope()
    {
        _context.Envelope.Returns((Envelope?)null);

        var consumeContext = new WolverineConsumeContext<TestMtCommand>(_context, new TestMtCommand("test"));

        consumeContext.Headers.Count.ShouldBe(0);
    }

    [Fact]
    public async Task publish_delegates_to_context_publish_async()
    {
        var consumeContext = new WolverineConsumeContext<TestMtCommand>(_context, new TestMtCommand("test"));
        var eventMessage = new TestMtEvent("created");

        await consumeContext.Publish(eventMessage);

        await _context.Received(1).PublishAsync(eventMessage, null);
    }

    [Fact]
    public async Task send_delegates_to_context_send_async()
    {
        var consumeContext = new WolverineConsumeContext<TestMtCommand>(_context, new TestMtCommand("test"));
        var command = new TestMtCommand("forward");

        await consumeContext.Send(command);

        await _context.Received(1).SendAsync(command, null);
    }

    [Fact]
    public async Task send_with_destination_uses_endpoint()
    {
        var destinationUri = new Uri("rabbitmq://localhost/remote-queue");
        var endpoint = Substitute.For<IDestinationEndpoint>();
        _context.EndpointFor(destinationUri).Returns(endpoint);

        var consumeContext = new WolverineConsumeContext<TestMtCommand>(_context, new TestMtCommand("test"));
        var command = new TestMtCommand("forward");

        await consumeContext.Send(command, destinationUri);

        await endpoint.Received(1).SendAsync(command, Arg.Any<DeliveryOptions?>());
    }

    [Fact]
    public async Task respond_async_delegates_to_respond_to_sender()
    {
        var consumeContext = new WolverineConsumeContext<TestMtCommand>(_context, new TestMtCommand("test"));
        var response = new TestMtResponse("ok");

        await consumeContext.RespondAsync(response);

        await _context.Received(1).RespondToSenderAsync(response);
    }
}

public class message_bus_implements_masstransit_shim_interfaces
{
    [Fact]
    public void message_bus_implements_publish_endpoint()
    {
        typeof(IPublishEndpoint).IsAssignableFrom(typeof(Wolverine.Runtime.MessageBus))
            .ShouldBeTrue();
    }

    [Fact]
    public void message_bus_implements_send_endpoint_provider()
    {
        typeof(ISendEndpointProvider).IsAssignableFrom(typeof(Wolverine.Runtime.MessageBus))
            .ShouldBeTrue();
    }
}

public class consume_context_variable_source_tests
{
    private readonly ConsumeContextVariableSource _source = new();

    [Fact]
    public void matches_consume_context_of_t()
    {
        _source.Matches(typeof(ConsumeContext<TestMtCommand>)).ShouldBeTrue();
    }

    [Fact]
    public void does_not_match_unrelated_type()
    {
        _source.Matches(typeof(string)).ShouldBeFalse();
    }

    [Fact]
    public void does_not_match_non_generic_type()
    {
        _source.Matches(typeof(IMessageBus)).ShouldBeFalse();
    }

    [Fact]
    public void creates_variable_of_correct_type()
    {
        var variable = _source.Create(typeof(ConsumeContext<TestMtCommand>));
        variable.VariableType.ShouldBe(typeof(ConsumeContext<TestMtCommand>));
    }
}

public class masstransit_interface_variable_source_tests
{
    private readonly MassTransitInterfaceVariableSource _source = new();

    [Fact]
    public void matches_publish_endpoint()
    {
        _source.Matches(typeof(IPublishEndpoint)).ShouldBeTrue();
    }

    [Fact]
    public void matches_send_endpoint_provider()
    {
        _source.Matches(typeof(ISendEndpointProvider)).ShouldBeTrue();
    }

    [Fact]
    public void does_not_match_unrelated_type()
    {
        _source.Matches(typeof(string)).ShouldBeFalse();
    }

    [Fact]
    public void creates_publish_endpoint_variable()
    {
        var variable = _source.Create(typeof(IPublishEndpoint));
        variable.VariableType.ShouldBe(typeof(IPublishEndpoint));
    }

    [Fact]
    public void creates_send_endpoint_provider_variable()
    {
        var variable = _source.Create(typeof(ISendEndpointProvider));
        variable.VariableType.ShouldBe(typeof(ISendEndpointProvider));
    }
}

// Test message types
public record TestMtCommand(string Data);
public record TestMtEvent(string Data);
public record TestMtResponse(string Data);
