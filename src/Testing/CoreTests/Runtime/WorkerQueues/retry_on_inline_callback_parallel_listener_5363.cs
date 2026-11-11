using NSubstitute;
using Shouldly;
using Wolverine;
using Wolverine.Runtime;
using Wolverine.Transports;
using Xunit;

namespace CoreTests.Runtime.WorkerQueues;

public class retry_on_inline_callback_parallel_listener_5363
{
    [Fact]
    public async Task complete_and_defer_do_not_recurse_through_a_parallel_listener()
    {
        var runtime = Substitute.For<IWolverineRuntime>();
        var inbox = Substitute.For<Wolverine.Persistence.Durability.IMessageInbox>();
        runtime.Storage.Inbox.Returns(inbox);

        var parallel = new ParallelListener(new Uri("stub://one"), new[] { Substitute.For<IListener>() });
        var callback = new RetryOnInlineChannelCallback(parallel, runtime);
        var envelope = new Envelope(new object());
        envelope.Listener = callback;

        // Before the fix this recursed until the stack or heap gave out
        await callback.CompleteAsync(envelope).AsTask().WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
        await callback.DeferAsync(envelope).AsTask().WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);

        await inbox.Received(1).MarkIncomingEnvelopeAsHandledAsync(envelope);
    }
}
