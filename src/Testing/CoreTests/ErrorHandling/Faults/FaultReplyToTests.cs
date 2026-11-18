using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Wolverine;
using Wolverine.ErrorHandling;
using Wolverine.Runtime;
using Xunit;

namespace CoreTests.ErrorHandling.Faults;

// GH-5553: the fault of a message goes to the address its sender asked for, with or without fault events
public class FaultReplyToTests
{
    private record Foo(string Name);

    private static readonly Uri Sender = new("tcp://localhost:2201");

    private static (FaultPublisher publisher, FaultPublishingPolicy policy, IEnvelopeLifecycle lifecycle, IDestinationEndpoint replyEndpoint)
        createPublisher(params Uri[] routedTo)
    {
        var policy = new FaultPublishingPolicy();
        var lifecycle = Substitute.For<IEnvelopeLifecycle>();

        var runtime = Substitute.For<IWolverineRuntime>();
        var router = Substitute.For<Wolverine.Runtime.Routing.IMessageRouter>();
        runtime.RoutingFor(Arg.Any<Type>()).Returns(router);
        router.RouteForPublish(Arg.Any<object>(), Arg.Any<DeliveryOptions?>())
            .Returns(routedTo.Select(x => new Envelope { Destination = x }).ToArray());

        var replyEndpoint = Substitute.For<IDestinationEndpoint>();
        lifecycle.EndpointFor(Arg.Any<Uri>()).Returns(replyEndpoint);

        var publisher = new FaultPublisher(policy, runtime, NullLogger<FaultPublisher>.Instance,
            new System.Diagnostics.Metrics.Meter("FaultReplyToTests"));
        return (publisher, policy, lifecycle, replyEndpoint);
    }

    private static Envelope failed(string? replyTo, Uri? replyUri = null)
    {
        var envelope = new Envelope
        {
            Message = new Foo("a"),
            Id = Guid.NewGuid(),
            ReplyUri = replyUri,
            Destination = new Uri("tcp://localhost:2202")
        };

        if (replyTo != null) envelope.Headers[FaultHeaders.ReplyTo] = replyTo;
        return envelope;
    }

    [Fact]
    public void FaultTo_names_the_address()
    {
        var options = new DeliveryOptions().FaultTo(Sender);
        options.Headers[FaultHeaders.ReplyTo].ShouldBe(Sender.ToString());
    }

    [Fact]
    public void ReturnFaultToSender_names_the_reply_address_once_the_route_knows_it()
    {
        var options = new DeliveryOptions().ReturnFaultToSender();
        options.Headers[FaultHeaders.ReplyTo].ShouldBe(FaultHeaders.ReplyToSender);

        var envelope = new Envelope { ReplyUri = Sender };
        options.Override(envelope);
        envelope.Headers[FaultHeaders.ReplyTo].ShouldBe(Sender.ToString());

        // No reply address yet: the receiver reads the envelope's reply address instead
        var unknown = new Envelope();
        options.Override(unknown);
        unknown.Headers[FaultHeaders.ReplyTo].ShouldBe(FaultHeaders.ReplyToSender);
    }

    [Fact]
    public async Task with_fault_events_off_the_fault_goes_only_to_the_address_the_sender_named()
    {
        var (publisher, _, lifecycle, replyEndpoint) = createPublisher(new Uri("local://audit"));
        lifecycle.Envelope.Returns(failed(Sender.ToString()));

        await publisher.PublishIfEnabledAsync(lifecycle, new Exception("boom"), FaultTrigger.MovedToErrorQueue, null);

        lifecycle.Received(1).EndpointFor(Sender);
        await replyEndpoint.Received(1).SendAsync(
            Arg.Is<Fault<Foo>>(x => x.Message.Name == "a" && x.Exception.Message == "boom"),
            Arg.Is<DeliveryOptions?>(x => x!.Headers[FaultHeaders.AutoPublished] == "true"));
        await lifecycle.DidNotReceive().PublishAsync(Arg.Any<object>(), Arg.Any<DeliveryOptions?>());
    }

    [Fact]
    public async Task the_sender_value_reads_the_envelopes_reply_address()
    {
        var (publisher, _, lifecycle, replyEndpoint) = createPublisher();
        lifecycle.Envelope.Returns(failed(FaultHeaders.ReplyToSender, Sender));

        await publisher.PublishIfEnabledAsync(lifecycle, new Exception(), FaultTrigger.MovedToErrorQueue, null);

        lifecycle.Received(1).EndpointFor(Sender);
        await replyEndpoint.Received(1).SendAsync(Arg.Any<Fault<Foo>>(), Arg.Any<DeliveryOptions?>());
    }

    [Fact]
    public async Task no_fault_is_sent_back_to_the_receivers_own_address()
    {
        var (publisher, _, lifecycle, _) = createPublisher();
        var envelope = failed(FaultHeaders.ReplyToSender);

        // What a receiver fills in when an envelope arrives with no reply address
        envelope.ReplyUri = envelope.Destination;
        lifecycle.Envelope.Returns(envelope);

        await publisher.PublishIfEnabledAsync(lifecycle, new Exception(), FaultTrigger.MovedToErrorQueue, null);

        lifecycle.DidNotReceive().EndpointFor(Arg.Any<Uri>());
    }

    [Fact]
    public async Task a_discarded_message_sends_no_fault_back()
    {
        var (publisher, _, lifecycle, _) = createPublisher();
        lifecycle.Envelope.Returns(failed(Sender.ToString()));

        await publisher.PublishIfEnabledAsync(lifecycle, new Exception(), FaultTrigger.Discarded, null);

        lifecycle.DidNotReceive().EndpointFor(Arg.Any<Uri>());
    }

    [Fact]
    public async Task with_fault_events_on_the_fault_goes_to_the_routes_and_the_address()
    {
        var (publisher, policy, lifecycle, replyEndpoint) = createPublisher(new Uri("rabbitmq://queue/faults"));
        policy.GlobalMode = FaultPublishingMode.DlqOnly;
        lifecycle.Envelope.Returns(failed(Sender.ToString()));

        await publisher.PublishIfEnabledAsync(lifecycle, new Exception(), FaultTrigger.MovedToErrorQueue, null);

        await lifecycle.Received(1).PublishAsync(Arg.Any<Fault<Foo>>(), Arg.Any<DeliveryOptions?>());
        await replyEndpoint.Received(1).SendAsync(Arg.Any<Fault<Foo>>(), Arg.Any<DeliveryOptions?>());
    }

    [Fact]
    public async Task with_fault_events_on_an_address_a_route_already_reaches_gets_the_fault_once()
    {
        var (publisher, policy, lifecycle, _) = createPublisher(Sender);
        policy.GlobalMode = FaultPublishingMode.DlqOnly;
        lifecycle.Envelope.Returns(failed(Sender.ToString()));

        await publisher.PublishIfEnabledAsync(lifecycle, new Exception(), FaultTrigger.MovedToErrorQueue, null);

        await lifecycle.Received(1).PublishAsync(Arg.Any<Fault<Foo>>(), Arg.Any<DeliveryOptions?>());
        lifecycle.DidNotReceive().EndpointFor(Arg.Any<Uri>());
    }

    [Fact]
    public async Task two_local_queues_reach_the_same_handlers_so_the_fault_goes_once()
    {
        var (publisher, policy, lifecycle, _) = createPublisher(new Uri("local://coretests.foo"));
        policy.GlobalMode = FaultPublishingMode.DlqOnly;
        lifecycle.Envelope.Returns(failed("local://replies"));

        await publisher.PublishIfEnabledAsync(lifecycle, new Exception(), FaultTrigger.MovedToErrorQueue, null);

        await lifecycle.Received(1).PublishAsync(Arg.Any<Fault<Foo>>(), Arg.Any<DeliveryOptions?>());
        lifecycle.DidNotReceive().EndpointFor(Arg.Any<Uri>());
    }

    [Fact]
    public async Task the_fault_of_a_fault_is_never_sent_back()
    {
        var (publisher, _, lifecycle, _) = createPublisher();
        var envelope = failed(Sender.ToString());
        envelope.Message = new Fault<Foo>(new Foo("a"), ExceptionInfo.From(new Exception()), 1, DateTimeOffset.UtcNow,
            null, Guid.Empty, null, null, new Dictionary<string, string?>());
        lifecycle.Envelope.Returns(envelope);

        await publisher.PublishIfEnabledAsync(lifecycle, new Exception(), FaultTrigger.MovedToErrorQueue, null);

        lifecycle.DidNotReceive().EndpointFor(Arg.Any<Uri>());
    }
}
