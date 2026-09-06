using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Shouldly;
using Wolverine;
using Wolverine.Configuration;
using Wolverine.Logging;
using Wolverine.Persistence.Durability;
using Wolverine.Runtime;
using Wolverine.Transports;
using Wolverine.Transports.Sending;
using Xunit;

namespace CoreTests.Persistence.Durability;

/// <summary>
/// GH-4319. The non-transactional outbox paid one connection and one INSERT per envelope on the way
/// out, and a second single-row DELETE per envelope on the way back -- while the many-envelope forms
/// of both already existed. These now coalesce.
///
/// <para>
/// The load-bearing assertion in here is <see cref="a_lone_send_is_stored_immediately" />. The outbox
/// store gates the send, so any batching WINDOW in front of it would delay delivery rather than
/// bookkeeping; GH-3490 measured that shape at a 5,767ms transit p50. A lone envelope going straight
/// through is the property that makes this design safe, not an optimization detail.
/// </para>
/// </summary>
public class coalesced_outbox_storage_4319 : IAsyncDisposable
{
    private readonly IMessageOutbox _outbox = Substitute.For<IMessageOutbox>();
    private readonly Uri _destination = new("stub://one");
    private readonly ISender _sender = Substitute.For<ISender>();
    private DurableSendingAgent? _agent;

    public coalesced_outbox_storage_4319()
    {
        _sender.Destination.Returns(_destination);
        _sender.SupportsNativeScheduledSend.Returns(true);
    }

    private DurableSendingAgent agent(int batchSize = 100)
    {
        var settings = new DurabilitySettings { StoreOutgoingBatchSize = batchSize };
        _agent = new DurableSendingAgent(_sender, settings, NullLogger.Instance,
            Substitute.For<IMessageTracker>(), _outbox, new CoalesceEndpoint(_destination));
        return _agent;
    }

    private class CoalesceEndpoint : Endpoint
    {
        public CoalesceEndpoint(Uri uri) : base(uri, EndpointRole.Application)
        {
        }

        public override ValueTask<IListener> BuildListenerAsync(IWolverineRuntime runtime, IReceiver receiver)
            => throw new NotSupportedException();

        protected override ISender CreateSender(IWolverineRuntime runtime) => throw new NotSupportedException();

        protected override bool supportsMode(EndpointMode mode) => true;
    }

    private static Envelope envelope() => new()
    {
        Id = Guid.NewGuid(), Data = [1, 2, 3], MessageType = "some.message", Destination = new Uri("stub://one")
    };

    /// <summary>
    /// Hold the first store open so everything sent meanwhile piles up behind it. Concurrency is the
    /// only thing that forms a batch here -- there is deliberately no timer to wait on.
    /// </summary>
    private (TaskCompletionSource gate, TaskCompletionSource started) gateTheFirstStore()
    {
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var first = true;

        _outbox.StoreOutgoingAsync(Arg.Any<Envelope>(), Arg.Any<int>()).Returns(_ =>
        {
            if (first)
            {
                first = false;
                started.TrySetResult();
                return gate.Task;
            }

            return Task.CompletedTask;
        });

        return (gate, started);
    }

    public async ValueTask DisposeAsync()
    {
        if (_agent != null) await _agent.DisposeAsync();
    }

    [Fact]
    public async Task a_lone_send_is_stored_immediately()
    {
        var theAgent = agent();
        var lone = envelope();

        await theAgent.StoreAndForwardAsync(lone);

        await _outbox.Received(1).StoreOutgoingAsync(lone, Arg.Any<int>());
        await _outbox.DidNotReceive().StoreOutgoingAsync(Arg.Any<IReadOnlyList<Envelope>>(), Arg.Any<int>());
    }

    [Fact]
    public async Task sends_that_arrive_during_a_store_share_the_next_batch()
    {
        var theAgent = agent();
        var (gate, started) = gateTheFirstStore();

        var first = theAgent.StoreAndForwardAsync(envelope()).AsTask();
        await started.Task;

        var others = Enumerable.Range(0, 5).Select(_ => envelope()).ToArray();
        var pending = others.Select(e => theAgent.StoreAndForwardAsync(e).AsTask()).ToArray();

        gate.SetResult();
        await Task.WhenAll(pending.Append(first)).WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);

        await _outbox.Received(1).StoreOutgoingAsync(
            Arg.Is<IReadOnlyList<Envelope>>(list => list.Count == 5 && others.All(list.Contains)),
            Arg.Any<int>());
    }

    /// <summary>
    /// The store gates the send, so an envelope must never reach the sender before its row exists --
    /// coalescing changes how many round trips there are, not that ordering.
    /// </summary>
    [Fact]
    public async Task nothing_reaches_the_sender_before_its_row_is_stored()
    {
        var theAgent = agent();
        var (gate, started) = gateTheFirstStore();

        var first = theAgent.StoreAndForwardAsync(envelope()).AsTask();
        await started.Task;

        await Task.Delay(100, TestContext.Current.CancellationToken);
        await _sender.DidNotReceive().SendAsync(Arg.Any<Envelope>());

        gate.SetResult();
        await first.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task a_batch_size_of_one_stores_every_envelope_on_its_own()
    {
        var theAgent = agent(batchSize: 1);
        var (gate, started) = gateTheFirstStore();

        var first = theAgent.StoreAndForwardAsync(envelope()).AsTask();
        await started.Task;

        var pending = Enumerable.Range(0, 4).Select(_ => theAgent.StoreAndForwardAsync(envelope()).AsTask()).ToArray();

        gate.SetResult();
        await Task.WhenAll(pending.Append(first)).WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);

        await _outbox.Received(5).StoreOutgoingAsync(Arg.Any<Envelope>(), Arg.Any<int>());
        await _outbox.DidNotReceive().StoreOutgoingAsync(Arg.Any<IReadOnlyList<Envelope>>(), Arg.Any<int>());
    }

    [Fact]
    public async Task a_failed_batch_store_falls_back_to_one_at_a_time()
    {
        var theAgent = agent();
        var (gate, started) = gateTheFirstStore();
        _outbox.StoreOutgoingAsync(Arg.Any<IReadOnlyList<Envelope>>(), Arg.Any<int>())
            .Returns(_ => Task.FromException(new InvalidOperationException("nope")));

        var first = theAgent.StoreAndForwardAsync(envelope()).AsTask();
        await started.Task;

        var others = Enumerable.Range(0, 4).Select(_ => envelope()).ToArray();
        var pending = others.Select(e => theAgent.StoreAndForwardAsync(e).AsTask()).ToArray();

        gate.SetResult();
        await Task.WhenAll(pending.Append(first)).WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);

        // Every envelope still stored, and still forwarded
        foreach (var e in others)
        {
            await _outbox.Received(1).StoreOutgoingAsync(e, Arg.Any<int>());
        }
    }

    /// <summary>
    /// The other half of GH-4319: MarkSuccessfulAsync(Envelope) used to pay a single-row DELETE per
    /// envelope even though DeleteOutgoingAsync(Envelope[]) already existed for the batched sends.
    /// </summary>
    [Fact]
    public async Task successful_single_sends_delete_their_rows_in_one_batch()
    {
        var theAgent = agent();

        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var first = true;

        _outbox.DeleteOutgoingAsync(Arg.Any<Envelope>()).Returns(_ =>
        {
            if (first)
            {
                first = false;
                started.TrySetResult();
                return gate.Task;
            }

            return Task.CompletedTask;
        });

        var lone = theAgent.MarkSuccessfulAsync(envelope());
        await started.Task;

        var others = Enumerable.Range(0, 4).Select(_ => envelope()).ToArray();
        var pending = others.Select(e => theAgent.MarkSuccessfulAsync(e)).ToArray();

        gate.SetResult();
        await Task.WhenAll(pending.Append(lone)).WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);

        await _outbox.Received(1).DeleteOutgoingAsync(
            Arg.Is<Envelope[]>(list => list.Length == 4 && others.All(list.Contains)));
    }
}
