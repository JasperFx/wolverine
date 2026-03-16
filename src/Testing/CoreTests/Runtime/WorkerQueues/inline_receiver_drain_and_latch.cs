using NSubstitute;
using Wolverine.ComplianceTests;
using Wolverine.Runtime;
using Wolverine.Runtime.WorkerQueues;
using Wolverine.Transports;
using Wolverine.Transports.Stub;
using Xunit;

namespace CoreTests.Runtime.WorkerQueues;

public class inline_receiver_drain_when_idle
{
    private readonly IHandlerPipeline thePipeline = Substitute.For<IHandlerPipeline>();
    private readonly MockWolverineRuntime theRuntime = new();
    private readonly InlineReceiver theReceiver;

    public inline_receiver_drain_when_idle()
    {
        var stubEndpoint = new StubEndpoint("one", new StubTransport());
        theReceiver = new InlineReceiver(stubEndpoint, theRuntime, thePipeline);
    }

    [Fact]
    public async Task drain_completes_immediately_when_no_messages_in_flight()
    {
        // DrainAsync should return immediately when nothing is in-flight
        var drainTask = theReceiver.DrainAsync();
        Assert.True(drainTask.IsCompleted);
        await drainTask;
    }

    [Fact]
    public void queue_count_is_zero_when_idle()
    {
        Assert.Equal(0, theReceiver.QueueCount);
    }
}

public class inline_receiver_drain_waits_for_in_flight
{
    private readonly IListener theListener = Substitute.For<IListener>();
    private readonly IHandlerPipeline thePipeline = Substitute.For<IHandlerPipeline>();
    private readonly MockWolverineRuntime theRuntime = new();
    private readonly InlineReceiver theReceiver;

    public inline_receiver_drain_waits_for_in_flight()
    {
        var stubEndpoint = new StubEndpoint("one", new StubTransport());
        theReceiver = new InlineReceiver(stubEndpoint, theRuntime, thePipeline);
        theListener.Address.Returns(new Uri("stub://one"));
    }

    [Fact]
    public async Task drain_waits_for_in_flight_message_to_complete()
    {
        var messageBlocking = new TaskCompletionSource();

        // Make pipeline.InvokeAsync block until we release it
        thePipeline.InvokeAsync(Arg.Any<Envelope>(), Arg.Any<IChannelCallback>(), Arg.Any<System.Diagnostics.Activity>())
            .Returns(async _ => await messageBlocking.Task);

        var envelope = ObjectMother.Envelope();

        // Start receiving on a background task — it will block in InvokeAsync
        var receiveTask = Task.Run(() => theReceiver.ReceivedAsync(theListener, envelope).AsTask());

        // Give the receive task time to enter the pipeline
        await Task.Delay(50);

        Assert.Equal(1, theReceiver.QueueCount);

        // Start drain — should NOT complete yet because a message is in-flight
        var drainTask = theReceiver.DrainAsync().AsTask();
        await Task.Delay(50);

        Assert.False(drainTask.IsCompleted, "DrainAsync should not complete while a message is in-flight");

        // Release the message
        messageBlocking.SetResult();
        await receiveTask;

        // Drain should now complete
        await drainTask.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.Equal(0, theReceiver.QueueCount);
    }
}

public class inline_receiver_latch_defers_messages
{
    private readonly IListener theListener = Substitute.For<IListener>();
    private readonly IHandlerPipeline thePipeline = Substitute.For<IHandlerPipeline>();
    private readonly MockWolverineRuntime theRuntime = new();
    private readonly InlineReceiver theReceiver;

    public inline_receiver_latch_defers_messages()
    {
        var stubEndpoint = new StubEndpoint("one", new StubTransport());
        theReceiver = new InlineReceiver(stubEndpoint, theRuntime, thePipeline);
        theListener.Address.Returns(new Uri("stub://one"));
    }

    [Fact]
    public async Task latched_receiver_defers_new_messages()
    {
        theReceiver.Latch();

        var envelope = ObjectMother.Envelope();
        await theReceiver.ReceivedAsync(theListener, envelope);

        // Should have deferred the message back to the listener
        await theListener.Received(1).DeferAsync(envelope);

        // Pipeline should NOT have been invoked
        await thePipeline.DidNotReceive()
            .InvokeAsync(Arg.Any<Envelope>(), Arg.Any<IChannelCallback>(), Arg.Any<System.Diagnostics.Activity>());
    }

    [Fact]
    public async Task latched_receiver_defers_batch_messages()
    {
        theReceiver.Latch();

        var envelope1 = ObjectMother.Envelope();
        var envelope2 = ObjectMother.Envelope();
        await theReceiver.ReceivedAsync(theListener, new[] { envelope1, envelope2 });

        // Both messages should have been deferred
        await theListener.Received(1).DeferAsync(envelope1);
        await theListener.Received(1).DeferAsync(envelope2);
    }

    [Fact]
    public async Task queue_count_stays_zero_for_latched_messages()
    {
        theReceiver.Latch();

        var envelope = ObjectMother.Envelope();
        await theReceiver.ReceivedAsync(theListener, envelope);

        Assert.Equal(0, theReceiver.QueueCount);
    }
}

public class inline_receiver_drain_respects_timeout
{
    private readonly IListener theListener = Substitute.For<IListener>();
    private readonly IHandlerPipeline thePipeline = Substitute.For<IHandlerPipeline>();
    private readonly MockWolverineRuntime theRuntime = new();
    private readonly InlineReceiver theReceiver;

    public inline_receiver_drain_respects_timeout()
    {
        // Set a very short drain timeout for this test
        theRuntime.DurabilitySettings.DrainTimeout = TimeSpan.FromMilliseconds(200);

        var stubEndpoint = new StubEndpoint("one", new StubTransport());
        theReceiver = new InlineReceiver(stubEndpoint, theRuntime, thePipeline);
        theListener.Address.Returns(new Uri("stub://one"));
    }

    [Fact]
    public async Task drain_times_out_when_message_blocks_forever()
    {
        // Make pipeline block forever
        thePipeline.InvokeAsync(Arg.Any<Envelope>(), Arg.Any<IChannelCallback>(), Arg.Any<System.Diagnostics.Activity>())
            .Returns(async _ => await Task.Delay(Timeout.Infinite));

        var envelope = ObjectMother.Envelope();

        // Start a receive that will block
        _ = Task.Run(() => theReceiver.ReceivedAsync(theListener, envelope).AsTask());
        await Task.Delay(50);

        // DrainAsync should time out (throw TimeoutException) per DrainTimeout
        await Assert.ThrowsAsync<TimeoutException>(() => theReceiver.DrainAsync().AsTask());
    }
}

public class inline_receiver_batch_drain_waits_for_all_messages
{
    private readonly IListener theListener = Substitute.For<IListener>();
    private readonly IHandlerPipeline thePipeline = Substitute.For<IHandlerPipeline>();
    private readonly MockWolverineRuntime theRuntime = new();
    private readonly InlineReceiver theReceiver;

    public inline_receiver_batch_drain_waits_for_all_messages()
    {
        var stubEndpoint = new StubEndpoint("one", new StubTransport());
        theReceiver = new InlineReceiver(stubEndpoint, theRuntime, thePipeline);
        theListener.Address.Returns(new Uri("stub://one"));
    }

    [Fact]
    public async Task drain_does_not_signal_until_all_batch_messages_are_handled()
    {
        var firstMessageBlocking = new TaskCompletionSource();

        // First InvokeAsync call blocks; subsequent calls won't be reached because
        // after we latch, remaining messages will be deferred instead of invoked.
        thePipeline.InvokeAsync(Arg.Any<Envelope>(), Arg.Any<IChannelCallback>(), Arg.Any<System.Diagnostics.Activity>())
            .Returns(async _ => await firstMessageBlocking.Task);

        var envelope1 = ObjectMother.Envelope();
        var envelope2 = ObjectMother.Envelope();
        var envelope3 = ObjectMother.Envelope();

        // Start batch receive on a background task — it will block on the first message
        var receiveTask = Task.Run(() => theReceiver.ReceivedAsync(theListener, new[] { envelope1, envelope2, envelope3 }).AsTask());

        // Give the receive task time to enter the pipeline for envelope1
        await Task.Delay(50);

        Assert.Equal(3, theReceiver.QueueCount);

        // Drain while the first message is still in-flight. This latches the receiver.
        var drainTask = theReceiver.DrainAsync().AsTask();
        await Task.Delay(50);

        Assert.False(drainTask.IsCompleted, "DrainAsync must not complete while batch messages are still in-flight");

        // Release the first message — the remaining two should be deferred (latched)
        firstMessageBlocking.SetResult();

        // Wait for the full batch receive to complete
        await receiveTask.WaitAsync(TimeSpan.FromSeconds(5));

        // Drain should now complete since all messages are processed/deferred
        await drainTask.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.Equal(0, theReceiver.QueueCount);

        // Remaining messages should have been deferred
        await theListener.Received(1).DeferAsync(envelope2);
        await theListener.Received(1).DeferAsync(envelope3);
    }
}
