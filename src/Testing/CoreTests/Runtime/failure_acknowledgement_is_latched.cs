using NSubstitute;
using Shouldly;
using Wolverine.ComplianceTests;
using Wolverine.Runtime;
using Wolverine.Runtime.RemoteInvocation;
using Wolverine.Runtime.Routing;
using Wolverine.Transports;
using Xunit;

namespace CoreTests.Runtime;

/// <summary>
/// The sender of an envelope gets at most one failure acknowledgement for it, however many error handling paths
/// try to send one
/// </summary>
public class failure_acknowledgement_is_latched
{
    private readonly MockWolverineRuntime theRuntime = new();
    private readonly IMessageRouter theRouter = Substitute.For<IMessageRouter>();
    private readonly Envelope theEnvelope = ObjectMother.Envelope();
    private readonly MessageContext theContext;

    public failure_acknowledgement_is_latched()
    {
        theRuntime.Routers[typeof(FailureAcknowledgement)] = theRouter;
        theRouter.RouteToDestination(Arg.Any<object>(), Arg.Any<Uri>(), Arg.Any<DeliveryOptions?>())
            .Returns(_ => ObjectMother.Envelope());

        theEnvelope.ReplyUri = new Uri("tcp://localhost:2222");

        theContext = new MessageContext(theRuntime);
        theContext.ReadEnvelope(theEnvelope, InvocationCallback.Instance);
    }

    [Fact]
    public async Task only_the_first_failure_ack_is_sent()
    {
        await theContext.SendFailureAcknowledgementAsync("first");
        await theContext.SendFailureAcknowledgementAsync("second");

        theRouter.Received(1).RouteToDestination(Arg.Any<object>(), theEnvelope.ReplyUri!, null);
        theRouter.Received(1).RouteToDestination(Arg.Is<FailureAcknowledgement>(x => x.Message == "first"),
            theEnvelope.ReplyUri!, null);
    }

    [Fact]
    public async Task no_reply_uri_does_not_latch()
    {
        theEnvelope.ReplyUri = null;

        await theContext.SendFailureAcknowledgementAsync("nobody to tell");

        theEnvelope.HasSentFailureAcknowledgement.ShouldBeFalse();
    }

    [Fact]
    public async Task a_pooled_envelope_is_reset()
    {
        await theContext.SendFailureAcknowledgementAsync("first");
        theEnvelope.HasSentFailureAcknowledgement.ShouldBeTrue();

        theEnvelope.Reset();

        theEnvelope.HasSentFailureAcknowledgement.ShouldBeFalse();
    }
}
