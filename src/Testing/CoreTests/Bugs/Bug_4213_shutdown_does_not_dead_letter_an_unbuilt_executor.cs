using System;
using System.Threading.Tasks;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Shouldly;
using Wolverine;
using Wolverine.Configuration;
using Wolverine.Runtime;
using Wolverine.Runtime.Handlers;
using Wolverine.Transports;
using Xunit;

namespace CoreTests.Bugs;

// GH-4213. Building the executor for a message type resolves services, so a node that is shutting down can
// hit it after the IServiceProvider is already disposed: IHost.Dispose() flags the provider *before* it
// disposes WolverineRuntime, whose DisposeAsync then drains whatever the receiver still holds. Every envelope
// caught in that window threw ObjectDisposedException out of HandlerChain.AttachTypesSynchronously and landed
// in the GH-4151 catch, which classifies an unbuildable executor as a permanent configuration error and
// dead-letters the envelope.
//
// Found through the Redis NativeAck suite, where a draining node dead-lettered five entries whose handlers
// never ran and the pending entries list went to zero -- the exact loss the mode exists to prevent. It is not
// a Redis defect: the classification is the pipeline's, and every transport reaches it.
//
// Shutdown is not a configuration error. The envelope has to be left unsettled so the broker (or the inbox)
// redelivers it to a live node, which is what InvokeAsync's own ObjectDisposedException guard already does --
// the GH-4151 catch was simply intercepting first.
public class Bug_4213_shutdown_does_not_dead_letter_an_unbuilt_executor
{
    private readonly Envelope theEnvelope = new() { Id = Guid.NewGuid(), Message = new Bug4213Ping() };

    [Fact]
    public async Task a_disposed_container_leaves_the_envelope_unsettled()
    {
        var channel = new RecordingChannel();

        await invokeAsync(channel, new ObjectDisposedException("IServiceProvider"));

        // Neither terminal: not acked away, and not dead-lettered. The broker still owns it.
        channel.CompleteCalls.ShouldBe(0);
        channel.DeadLetterCalls.ShouldBe(0);
    }

    [Fact]
    public async Task any_other_executor_build_failure_is_still_dead_lettered()
    {
        // GH-4151 unchanged: a chain that will never build is permanent, and the DLQ is where it belongs.
        var channel = new RecordingChannel();

        await invokeAsync(channel, new InvalidOperationException("this chain will never compile"));

        channel.DeadLetterCalls.ShouldBe(1);
    }

    private async Task invokeAsync(RecordingChannel channel, Exception buildFailure)
    {
        using var host = await Host.CreateDefaultBuilder()
            .UseWolverine(opts => opts.Discovery.DisableConventionalDiscovery().IncludeType<Bug4213PingHandler>())
            .StartAsync(TestContext.Current.CancellationToken);

        // A live runtime with a live cancellation token -- the point is the *container* being gone, not the
        // runtime having been asked to stop. A cancelled runtime short-circuits InvokeAsync before any of
        // this and would make the test vacuous.
        var runtime = (WolverineRuntime)host.Services.GetRequiredService<IWolverineRuntime>();
        var pipeline = new HandlerPipeline(runtime, new ThrowingExecutorFactory(buildFailure));

        // Nothing may escape the pipeline either way: an exception out of InvokeAsync faults the receiver
        // loop and stops the listener.
        await Should.NotThrowAsync(() => pipeline.InvokeAsync(theEnvelope, channel));
    }

    private sealed class ThrowingExecutorFactory(Exception failure) : IExecutorFactory
    {
        public IExecutor BuildFor(Type messageType) => throw failure;
        public IExecutor BuildFor(Type messageType, Endpoint endpoint) => throw failure;
    }

    private sealed class RecordingChannel : IChannelCallback, ISupportDeadLetterQueue
    {
        public int CompleteCalls { get; private set; }
        public int DeadLetterCalls { get; private set; }

        public IHandlerPipeline? Pipeline => null;
        public bool NativeDeadLetterQueueEnabled => true;

        public ValueTask CompleteAsync(Envelope envelope)
        {
            CompleteCalls++;
            return ValueTask.CompletedTask;
        }

        public ValueTask DeferAsync(Envelope envelope) => ValueTask.CompletedTask;

        public Task MoveToErrorsAsync(Envelope envelope, Exception exception)
        {
            DeadLetterCalls++;
            return Task.CompletedTask;
        }
    }
}

public record Bug4213Ping;

public class Bug4213PingHandler
{
    public static void Handle(Bug4213Ping ping)
    {
    }
}
