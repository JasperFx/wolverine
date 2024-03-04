using CoreTests.Runtime;
using TestingSupport;
using TestingSupport.Compliance;
using Wolverine.Configuration;
using Wolverine.Transports.Local;
using Xunit;

namespace CoreTests.Configuration;

public class SenderConfigurationTests
{
    [Fact]
    public void durably()
    {
        var endpoint = new LocalQueue("foo");

        endpoint.Mode.ShouldBe(EndpointMode.BufferedInMemory);

        var expression = new SubscriberConfiguration(endpoint);
        expression.UseDurableOutbox();
        endpoint.Compile(new MockWolverineRuntime());

        endpoint.Mode.ShouldBe(EndpointMode.Durable);
    }

    [Fact]
    public void buffered_in_memory()
    {
        var endpoint = new LocalQueue("foo");
        endpoint.Mode = EndpointMode.Durable;

        var expression = new SubscriberConfiguration(endpoint);
        expression.BufferedInMemory();

        endpoint.Compile(new MockWolverineRuntime());

        endpoint.Mode.ShouldBe(EndpointMode.BufferedInMemory);
    }

    [Fact]
    public void named()
    {
        var endpoint = new LocalQueue("foo");
        endpoint.Mode = EndpointMode.Durable;

        var expression = new SubscriberConfiguration(endpoint);
        expression.Named("FooEndpoint");
        endpoint.Compile(new MockWolverineRuntime());

        endpoint.EndpointName.ShouldBe("FooEndpoint");
    }

    [Fact]
    public void inline()
    {
        var endpoint = new LocalQueue("foo");
        endpoint.Mode = EndpointMode.Durable;

        var expression = new SubscriberConfiguration(endpoint);
        expression.SendInline();
        endpoint.Compile(new MockWolverineRuntime());

        endpoint.Mode.ShouldBe(EndpointMode.Inline);
    }


    [Fact]
    public void customize_envelope_rules()
    {
        var endpoint = new LocalQueue("foo");
        var expression = new SubscriberConfiguration(endpoint);
        expression.CustomizeOutgoing(e => e.Headers.Add("a", "one"));

        var envelope = ObjectMother.Envelope();

        endpoint.Compile(new MockWolverineRuntime());

        endpoint.ApplyEnvelopeRules(envelope);

        envelope.Headers["a"].ShouldBe("one");
    }


    [Fact]
    public void customize_per_specific_message_type()
    {
        var endpoint = new LocalQueue("foo");
        var expression = new SubscriberConfiguration(endpoint);
        expression.CustomizeOutgoingMessagesOfType<OtherMessage>(e => e.Headers.Add("g", "good"));

        endpoint.Compile(new MockWolverineRuntime());

        // Negative Case
        var envelope1 = new Envelope(new Message1());
        endpoint.ApplyEnvelopeRules(envelope1);

        envelope1.Headers.ContainsKey("g").ShouldBeFalse();


        // Positive Case
        var envelope2 = new Envelope(new OtherMessage());
        endpoint.ApplyEnvelopeRules(envelope2);

        envelope2.Headers["g"].ShouldBe("good");
    }


    [Fact]
    public void customize_per_specific_message_type_parent()
    {
        var endpoint = new LocalQueue("foo");
        var expression = new SubscriberConfiguration(endpoint);
        expression.CustomizeOutgoingMessagesOfType<BaseMessage>(e => e.Headers.Add("g", "good"));

        endpoint.Compile(new MockWolverineRuntime());

        // Negative Case
        var envelope1 = new Envelope(new Message1());
        endpoint.ApplyEnvelopeRules(envelope1);

        envelope1.Headers.ContainsKey("g").ShouldBeFalse();


        // Positive Case
        var envelope2 = new Envelope(new ExtendedMessage());
        endpoint.ApplyEnvelopeRules(envelope2);

        envelope2.Headers["g"].ShouldBe("good");
    }

    public abstract class BaseMessage
    {
    }

    public class ExtendedMessage : BaseMessage
    {
    }

    public class ColorMessage
    {
        public string Color { get; set; }
    }
}

public class OtherMessage
{
}