using JasperFx.Core;
using Microsoft.Extensions.Hosting;
using Shouldly;
using Wolverine.ComplianceTests.ErrorHandling.Faults;
using Wolverine.Tracking;
using Xunit;

namespace Wolverine.ComplianceTests.Compliance;

public abstract class TransportFaultRoutingCompliance : IAsyncLifetime
{
    protected IHost theSender = null!;
    protected IHost theReceiver = null!;
    protected readonly FaultSink theSink = new();

    /// <summary>
    /// Configure the Sender host:
    ///  - Listener for OrderPlaced that always fails (terminal on first attempt).
    ///  - PublishFaultEvents() globally.
    ///  - Routing of Fault&lt;OrderPlaced&gt; to a transport-specific queue/topic.
    /// </summary>
    public abstract Task<IHost> BuildSenderAsync();

    /// <summary>
    /// Configure the Receiver host:
    ///  - Subscribe to the same queue/topic the Sender publishes Fault&lt;OrderPlaced&gt; to.
    ///  - Register the supplied FaultSink as a singleton.
    ///  - Discover FaultSinkHandler.
    /// </summary>
    public abstract Task<IHost> BuildReceiverAsync(FaultSink sink);

    public async Task InitializeAsync()
    {
        theReceiver = await BuildReceiverAsync(theSink);
        theSender = await BuildSenderAsync();
    }

    public async Task DisposeAsync()
    {
        if (theSender is not null)
        {
            await theSender.StopAsync();
            theSender.Dispose();
        }
        if (theReceiver is not null)
        {
            await theReceiver.StopAsync();
            theReceiver.Dispose();
        }
    }

    [Fact]
    public async Task fault_is_received_by_subscriber_with_intact_payload()
    {
        var session = await theSender.TrackActivity()
            .DoNotAssertOnExceptionsDetected()
            .AlsoTrack(theReceiver)
            .Timeout(30.Seconds())
            .PublishMessageAndWaitAsync(new OrderPlaced("c-1"));

        // (A) Subscriber received the fault with intact body
        theSink.Captured.Count.ShouldBe(1);
        var captured = theSink.Captured.Single();
        captured.Message.OrderId.ShouldBe("c-1");
        captured.Exception.Type.ShouldBe(typeof(InvalidOperationException).FullName);

        // (B) Header survived broker round-trip
        var receivedRecord = session.Received
            .Envelopes()
            .Single(e => e.Message is Fault<OrderPlaced>);
        receivedRecord.Headers[FaultHeaders.AutoPublished].ShouldBe("true");
    }
}
