using NSubstitute;
using Wolverine.ComplianceTests;
using Wolverine.Configuration;
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
    public async Task drain_waits_for_in_flight_message_to_complete_when_latched()
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

        // Simulate shutdown: Latch() is called first, then DrainAsync()
        theReceiver.Latch();
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

    [Fact]
    public async Task drain_returns_immediately_without_prior_latch_to_avoid_deadlock()
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

        // Drain WITHOUT prior Latch() — simulates pause from within handler pipeline
        // (e.g., PauseListenerContinuation). Must return immediately to avoid deadlock.
        var drainTask = theReceiver.DrainAsync();
        Assert.True(drainTask.IsCompleted, "DrainAsync should return immediately without prior Latch() to avoid deadlock");

        // Clean up
        messageBlocking.SetResult();
        await receiveTask;
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

        // Simulate shutdown: Latch() first, then DrainAsync should time out
        theReceiver.Latch();
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

        // Simulate shutdown: Latch() first, then DrainAsync while the first message is still in-flight.
        theReceiver.Latch();
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

public class inline_receiver_process_inline_while_draining_processes_batch
{
    private readonly IListener theListener = Substitute.For<IListener>();
    private readonly IHandlerPipeline thePipeline = Substitute.For<IHandlerPipeline>();
    private readonly MockWolverineRuntime theRuntime = new();
    private readonly InlineReceiver theReceiver;

    public inline_receiver_process_inline_while_draining_processes_batch()
    {
        var stubEndpoint = new StubEndpoint("one", new StubTransport());
        stubEndpoint.ProcessInlineWhileDraining = true;
        theReceiver = new InlineReceiver(stubEndpoint, theRuntime, thePipeline);
        theListener.Address.Returns(new Uri("stub://one"));
    }

    [Fact]
    public async Task batch_messages_are_processed_not_deferred_while_draining()
    {
        var firstMessageBlocking = new TaskCompletionSource();

        thePipeline.InvokeAsync(Arg.Any<Envelope>(), Arg.Any<IChannelCallback>(), Arg.Any<System.Diagnostics.Activity>())
            .Returns(async _ => await firstMessageBlocking.Task);

        var envelope1 = ObjectMother.Envelope();
        var envelope2 = ObjectMother.Envelope();
        var envelope3 = ObjectMother.Envelope();

        // Start batch receive — it will block on the first message
        var receiveTask = Task.Run(() => theReceiver.ReceivedAsync(theListener, new[] { envelope1, envelope2, envelope3 }).AsTask());

        // Give the receive task time to enter the pipeline for envelope1
        await Task.Delay(50);

        Assert.Equal(3, theReceiver.QueueCount);

        // Simulate shutdown: Latch() first, then DrainAsync while the first message is still in-flight
        theReceiver.Latch();
        var drainTask = theReceiver.DrainAsync().AsTask();
        await Task.Delay(50);

        Assert.False(drainTask.IsCompleted, "DrainAsync must not complete while batch messages are still in-flight");

        // Release the first message — with ProcessInlineWhileDraining, remaining messages should be processed, not deferred
        firstMessageBlocking.SetResult();

        await receiveTask.WaitAsync(TimeSpan.FromSeconds(5));
        await drainTask.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.Equal(0, theReceiver.QueueCount);

        // Remaining messages should have been processed through the pipeline, NOT deferred
        await theListener.DidNotReceive().DeferAsync(envelope2);
        await theListener.DidNotReceive().DeferAsync(envelope3);

        // All three messages should have been invoked through the pipeline
        await thePipeline.Received(3).InvokeAsync(Arg.Any<Envelope>(), Arg.Any<IChannelCallback>(), Arg.Any<System.Diagnostics.Activity>());
    }
}

public class inline_receiver_process_inline_while_draining_defers_after_drain_completes
{
    private readonly IListener theListener = Substitute.For<IListener>();
    private readonly IHandlerPipeline thePipeline = Substitute.For<IHandlerPipeline>();
    private readonly MockWolverineRuntime theRuntime = new();
    private readonly InlineReceiver theReceiver;

    public inline_receiver_process_inline_while_draining_defers_after_drain_completes()
    {
        var stubEndpoint = new StubEndpoint("one", new StubTransport());
        stubEndpoint.ProcessInlineWhileDraining = true;
        theReceiver = new InlineReceiver(stubEndpoint, theRuntime, thePipeline);
        theListener.Address.Returns(new Uri("stub://one"));
    }

    [Fact]
    public async Task messages_are_deferred_after_drain_has_completed()
    {
        // Latch and drain with nothing in flight — drain completes immediately
        theReceiver.Latch();
        await theReceiver.DrainAsync();

        var envelope = ObjectMother.Envelope();
        await theReceiver.ReceivedAsync(theListener, envelope);

        // After drain completed, messages should be deferred even with the flag on
        await theListener.Received(1).DeferAsync(envelope);

        await thePipeline.DidNotReceive()
            .InvokeAsync(Arg.Any<Envelope>(), Arg.Any<IChannelCallback>(), Arg.Any<System.Diagnostics.Activity>());
    }
}

public class inline_receiver_process_inline_while_draining_non_wait_drain
{
    private readonly IListener theListener = Substitute.For<IListener>();
    private readonly IHandlerPipeline thePipeline = Substitute.For<IHandlerPipeline>();
    private readonly MockWolverineRuntime theRuntime = new();
    private readonly InlineReceiver theReceiver;

    public inline_receiver_process_inline_while_draining_non_wait_drain()
    {
        var stubEndpoint = new StubEndpoint("one", new StubTransport());
        stubEndpoint.ProcessInlineWhileDraining = true;
        theReceiver = new InlineReceiver(stubEndpoint, theRuntime, thePipeline);
        theListener.Address.Returns(new Uri("stub://one"));
    }

    [Fact]
    public async Task messages_are_processed_during_non_wait_drain()
    {
        var firstMessageBlocking = new TaskCompletionSource();

        thePipeline.InvokeAsync(Arg.Any<Envelope>(), Arg.Any<IChannelCallback>(), Arg.Any<System.Diagnostics.Activity>())
            .Returns(async _ => await firstMessageBlocking.Task);

        var envelope1 = ObjectMother.Envelope();
        var envelope2 = ObjectMother.Envelope();

        // Start batch receive — it will block on the first message
        var receiveTask = Task.Run(() => theReceiver.ReceivedAsync(theListener, new[] { envelope1, envelope2 }).AsTask());
        await Task.Delay(50);

        // DrainAsync without prior Latch() — returns immediately (non-wait path)
        var drainTask = theReceiver.DrainAsync();
        Assert.True(drainTask.IsCompleted, "DrainAsync should return immediately without prior Latch()");

        // Release the first message — envelope2 should still be processed
        firstMessageBlocking.SetResult();
        await receiveTask.WaitAsync(TimeSpan.FromSeconds(5));

        // Both messages should have been processed, not deferred
        await theListener.DidNotReceive().DeferAsync(envelope2);
        await thePipeline.Received(2).InvokeAsync(Arg.Any<Envelope>(), Arg.Any<IChannelCallback>(), Arg.Any<System.Diagnostics.Activity>());
    }
}
