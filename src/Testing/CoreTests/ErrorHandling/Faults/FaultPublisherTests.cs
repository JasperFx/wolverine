using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Wolverine;
using Wolverine.ErrorHandling;
using Xunit;

namespace CoreTests.ErrorHandling.Faults;

public class FaultPublisherTests
{
    private record Foo(string Name);

    private record struct ValueMessage(int N);

    private static (FaultPublisher publisher, FaultPublishingPolicy policy, IEnvelopeLifecycle lifecycle)
        CreatePublisher()
    {
        var policy = new FaultPublishingPolicy();
        var lifecycle = Substitute.For<IEnvelopeLifecycle>();
        var meter = new System.Diagnostics.Metrics.Meter("FaultPublisherTests");
        var publisher = new FaultPublisher(policy, NullLogger<FaultPublisher>.Instance, meter);
        return (publisher, policy, lifecycle);
    }

    private static Envelope EnvelopeFor(object message)
    {
        var env = new Envelope
        {
            Message = message,
            Id = Guid.NewGuid(),
            CorrelationId = Guid.NewGuid().ToString(),
            ConversationId = Guid.NewGuid(),
            Source = "tests",
            Attempts = 3,
            TenantId = "tenant-x",
        };
        env.Headers["x-custom"] = "v";
        return env;
    }

    [Fact]
    public async Task no_op_when_mode_is_none()
    {
        var (publisher, _, lifecycle) = CreatePublisher();
        lifecycle.Envelope.Returns(EnvelopeFor(new Foo("a")));

        await publisher.PublishIfEnabledAsync(lifecycle, new Exception(), FaultTrigger.MovedToErrorQueue);

        await lifecycle.DidNotReceive().PublishAsync(Arg.Any<object>(), Arg.Any<DeliveryOptions?>());
    }

    [Fact]
    public async Task publishes_fault_with_auto_header_when_dlq_only_and_trigger_is_dlq()
    {
        var (publisher, policy, lifecycle) = CreatePublisher();
        policy.PerTypeOverrides[typeof(Foo)] = FaultPublishingMode.DlqOnly;
        lifecycle.Envelope.Returns(EnvelopeFor(new Foo("a")));

        await publisher.PublishIfEnabledAsync(lifecycle, new InvalidOperationException("boom"),
            FaultTrigger.MovedToErrorQueue);

        await lifecycle.Received(1).PublishAsync(
            Arg.Is<Fault<Foo>>(f => f.Message.Name == "a" && f.Exception.Message == "boom"),
            Arg.Is<DeliveryOptions>(o => o.Headers[FaultHeaders.AutoPublished] == "true"));
    }

    [Fact]
    public async Task no_op_on_discard_when_dlq_only()
    {
        var (publisher, policy, lifecycle) = CreatePublisher();
        policy.PerTypeOverrides[typeof(Foo)] = FaultPublishingMode.DlqOnly;
        lifecycle.Envelope.Returns(EnvelopeFor(new Foo("a")));

        await publisher.PublishIfEnabledAsync(lifecycle, new Exception(), FaultTrigger.Discarded);

        await lifecycle.DidNotReceive().PublishAsync(Arg.Any<object>(), Arg.Any<DeliveryOptions?>());
    }

    [Fact]
    public async Task publishes_on_discard_when_dlq_and_discard()
    {
        var (publisher, policy, lifecycle) = CreatePublisher();
        policy.PerTypeOverrides[typeof(Foo)] = FaultPublishingMode.DlqAndDiscard;
        lifecycle.Envelope.Returns(EnvelopeFor(new Foo("a")));

        await publisher.PublishIfEnabledAsync(lifecycle, new Exception("oops"), FaultTrigger.Discarded);

        await lifecycle.Received(1).PublishAsync(
            Arg.Any<Fault<Foo>>(),
            Arg.Any<DeliveryOptions?>());
    }

    [Fact]
    public async Task publish_failure_is_swallowed_and_does_not_throw()
    {
        var policy = new FaultPublishingPolicy();
        policy.PerTypeOverrides[typeof(Foo)] = FaultPublishingMode.DlqOnly;
        var lifecycle = Substitute.For<IEnvelopeLifecycle>();
        var logger = Substitute.For<ILogger<FaultPublisher>>();
        var meter = new System.Diagnostics.Metrics.Meter("FaultPublisherTests");
        var publisher = new FaultPublisher(policy, logger, meter);

        lifecycle.Envelope.Returns(EnvelopeFor(new Foo("a")));
        lifecycle
            .When(x => x.PublishAsync(Arg.Any<object>(), Arg.Any<DeliveryOptions?>()))
            .Do(_ => throw new InvalidOperationException("transport down"));

        await publisher.PublishIfEnabledAsync(lifecycle, new Exception(), FaultTrigger.MovedToErrorQueue);

        logger.Received(1).Log(
            LogLevel.Error,
            Arg.Any<EventId>(),
            Arg.Any<object>(),
            Arg.Any<InvalidOperationException>(),
            Arg.Any<Func<object, Exception?, string>>());
    }

    [Fact]
    public async Task no_op_when_envelope_message_is_null()
    {
        var (publisher, policy, lifecycle) = CreatePublisher();
        policy.GlobalMode = FaultPublishingMode.DlqOnly;
        var env = EnvelopeFor(new Foo("a"));
        env.Message = null;
        lifecycle.Envelope.Returns(env);

        await publisher.PublishIfEnabledAsync(lifecycle, new Exception(), FaultTrigger.MovedToErrorQueue);

        await lifecycle.DidNotReceive().PublishAsync(Arg.Any<object>(), Arg.Any<DeliveryOptions?>());
    }

    [Fact]
    public async Task fault_carries_envelope_metadata()
    {
        var (publisher, policy, lifecycle) = CreatePublisher();
        policy.PerTypeOverrides[typeof(Foo)] = FaultPublishingMode.DlqOnly;
        var env = EnvelopeFor(new Foo("a"));
        lifecycle.Envelope.Returns(env);

        Fault<Foo>? captured = null;
        lifecycle
            .When(x => x.PublishAsync(Arg.Any<Fault<Foo>>(), Arg.Any<DeliveryOptions?>()))
            .Do(call => captured = call.Arg<Fault<Foo>>());

        await publisher.PublishIfEnabledAsync(lifecycle, new Exception("kaput"), FaultTrigger.MovedToErrorQueue);

        captured.ShouldNotBeNull();
        captured!.Attempts.ShouldBe(3);
        captured.CorrelationId.ShouldBe(env.CorrelationId);
        captured.ConversationId.ShouldBe(env.ConversationId);
        captured.TenantId.ShouldBe("tenant-x");
        captured.Source.ShouldBe("tests");
        captured.Headers["x-custom"].ShouldBe("v");
    }

    [Fact]
    public async Task no_op_when_message_type_is_value_type()
    {
        var (publisher, policy, lifecycle) = CreatePublisher();
        policy.GlobalMode = FaultPublishingMode.DlqOnly;

        var env = new Envelope
        {
            Message = new ValueMessage(42),
            Id = Guid.NewGuid(),
        };
        lifecycle.Envelope.Returns(env);

        await publisher.PublishIfEnabledAsync(lifecycle, new Exception(), FaultTrigger.MovedToErrorQueue);

        await lifecycle.DidNotReceive().PublishAsync(Arg.Any<object>(), Arg.Any<DeliveryOptions?>());
        // No exceptions thrown, no log entries added — silent no-op.
    }

    [Fact]
    public async Task fault_preserves_headers_with_null_values()
    {
        var (publisher, policy, lifecycle) = CreatePublisher();
        policy.PerTypeOverrides[typeof(Foo)] = FaultPublishingMode.DlqOnly;
        var env = new Envelope
        {
            Message = new Foo("a"),
            Id = Guid.NewGuid(),
            ConversationId = Guid.NewGuid(),
            Source = "tests",
            Attempts = 1,
        };
        env.Headers["nullable-header"] = null;
        lifecycle.Envelope.Returns(env);

        Fault<Foo>? captured = null;
        lifecycle
            .When(x => x.PublishAsync(Arg.Any<Fault<Foo>>(), Arg.Any<DeliveryOptions?>()))
            .Do(call => captured = call.Arg<Fault<Foo>>());

        await publisher.PublishIfEnabledAsync(lifecycle, new Exception(), FaultTrigger.MovedToErrorQueue);

        captured.ShouldNotBeNull();
        captured!.Headers.ContainsKey("nullable-header").ShouldBeTrue();
        captured.Headers["nullable-header"].ShouldBeNull();
    }
}
