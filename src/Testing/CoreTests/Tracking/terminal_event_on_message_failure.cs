using Microsoft.Extensions.Hosting;
using Shouldly;
using Wolverine;
using Wolverine.Attributes;
using Wolverine.Tracking;
using Xunit;

namespace CoreTests.Tracking;

// A message that dies in HandlerPipeline's last line of defense -- here, a sticky-handler
// misconfiguration that makes HandlerGraph.HandlerFor throw NoHandlerForEndpointException before any
// executor exists -- used to leave no terminal record at all. The recovery path acked the envelope
// away and called LogException, which is free-form text rather than a message event, so:
//
//   * failure metrics under-counted every envelope that died there, and
//   * a tracked session never saw the envelope reach a terminal state, so it could only ever end by
//     timing out.
//
// The second half compounded with WolverineRuntime.MessageFailed recording MessageEventType.Sent
// instead of MessageFailed -- Sent is not terminal, it only completes once a matching Received
// arrives. Both are fixed here.
public class terminal_event_on_message_failure
{
    [Fact]
    public async Task a_message_that_dies_in_the_pipeline_reaches_a_terminal_state()
    {
        using var host = await Host.CreateDefaultBuilder()
            .UseWolverine(opts =>
            {
                // Two sticky handlers and no unsticky one, so HandlerGraph.HandlerFor(type, endpoint)
                // has nothing to hand back for any *other* endpoint and throws
                opts.Discovery.DisableConventionalDiscovery()
                    .IncludeType(typeof(GreenUnstickyHandler))
                    .IncludeType(typeof(BlueUnstickyHandler));
            }).StartAsync(TestContext.Current.CancellationToken);

        var session = await host.TrackActivity()
            .DoNotAssertOnExceptionsDetected()
            .ExecuteAndWaitAsync(c =>
                c.EndpointFor(new Uri("local://maroon")).SendAsync(new UnstickyMessage()).AsTask());

        // Before the fix this could only end as TimedOut, five seconds later
        session.Status.ShouldBe(TrackingStatus.Completed);

        session.AllRecordsInOrder()
            .Any(x => x.MessageEventType == MessageEventType.MessageFailed)
            .ShouldBeTrue("the failed envelope should record a terminal MessageFailed event");
    }
}

public record UnstickyMessage;

[StickyHandler("green")]
public static class GreenUnstickyHandler
{
    public static void Handle(UnstickyMessage message)
    {
    }
}

[StickyHandler("blue")]
public static class BlueUnstickyHandler
{
    public static void Handle(UnstickyMessage message)
    {
    }
}
