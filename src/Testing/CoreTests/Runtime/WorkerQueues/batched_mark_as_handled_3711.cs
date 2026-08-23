using JasperFx.Core;
using NSubstitute;
using NSubstitute.ExceptionExtensions;
using Shouldly;
using Wolverine.ComplianceTests;
using Wolverine.Persistence.Durability;
using Wolverine.Runtime;
using Wolverine.Runtime.WorkerQueues;
using Wolverine.Transports;
using Wolverine.Transports.Stub;
using Xunit;

namespace CoreTests.Runtime.WorkerQueues;

/// <summary>
/// GH-3711 (O1b). DurableReceiver used to issue one MarkIncomingEnvelopeAsHandledAsync(Envelope) per
/// completion; now completions are coalesced behind a short max-age window into the IReadOnlyList
/// overload, the way the inbox INSERT already is.
/// </summary>
public class batched_mark_as_handled_3711 : IAsyncDisposable
{
    private readonly IListener theListener = Substitute.For<IListener>();
    private readonly IHandlerPipeline thePipeline = Substitute.For<IHandlerPipeline>();
    private readonly MockWolverineRuntime theRuntime = new();
    private DurableReceiver? theReceiver;

    private DurableReceiver buildReceiver(int batchSize = 100)
    {
        theRuntime.DurabilitySettings.MarkAsHandledBatchSize = batchSize;
        var endpoint = new StubEndpoint("one", new StubTransport());
        theReceiver = new DurableReceiver(endpoint, theRuntime, thePipeline);
        return theReceiver;
    }

    private static Envelope envelope()
    {
        var envelope = ObjectMother.Envelope();
        envelope.Status = EnvelopeStatus.Incoming;
        return envelope;
    }

    public async ValueTask DisposeAsync()
    {
        if (theReceiver != null)
        {
            await theReceiver.DisposeAsync();
        }
    }

    [Fact]
    public async Task completions_inside_the_window_are_marked_handled_as_one_batch()
    {
        var receiver = buildReceiver();
        var envelopes = Enumerable.Range(0, 5).Select(_ => envelope()).ToArray();

        foreach (var e in envelopes)
        {
            await receiver.CompleteAsync(e);
        }

        await receiver.DrainAsync();

        // One round trip, not five
        await theRuntime.Storage.Inbox.Received(1)
            .MarkIncomingEnvelopeAsHandledAsync(Arg.Is<IReadOnlyList<Envelope>>(list =>
                list.Count == 5 && envelopes.All(list.Contains)));
        await theRuntime.Storage.Inbox.DidNotReceive().MarkIncomingEnvelopeAsHandledAsync(Arg.Any<Envelope>());
    }

    [Fact]
    public async Task a_lone_completion_is_flushed_by_the_window_not_held_for_a_full_batch()
    {
        var receiver = buildReceiver();
        var lone = envelope();

        await receiver.CompleteAsync(lone);

        // No drain, no other completions: the max-age timer has to ship it on its own. A batch of one
        // takes the per-envelope path.
        var deadline = DateTimeOffset.UtcNow.AddSeconds(5);
        while (DateTimeOffset.UtcNow < deadline)
        {
            if (theRuntime.Storage.Inbox.ReceivedCalls().Any(c => c.GetMethodInfo().Name == nameof(IMessageInbox.MarkIncomingEnvelopeAsHandledAsync)))
            {
                break;
            }

            await Task.Delay(10, TestContext.Current.CancellationToken);
        }

        await theRuntime.Storage.Inbox.Received(1).MarkIncomingEnvelopeAsHandledAsync(lone);
    }

    [Fact]
    public async Task a_full_batch_flushes_on_size_before_the_window()
    {
        var receiver = buildReceiver(batchSize: 3);
        var envelopes = Enumerable.Range(0, 6).Select(_ => envelope()).ToArray();

        foreach (var e in envelopes)
        {
            await receiver.CompleteAsync(e);
        }

        await receiver.DrainAsync();

        await theRuntime.Storage.Inbox.Received(2)
            .MarkIncomingEnvelopeAsHandledAsync(Arg.Is<IReadOnlyList<Envelope>>(list => list.Count == 3));
    }

    [Fact]
    public async Task envelopes_already_marked_handled_by_transactional_middleware_are_skipped()
    {
        var receiver = buildReceiver();
        var fresh = envelope();
        var alreadyHandled = envelope();
        alreadyHandled.Status = EnvelopeStatus.Handled;
        var alsoFresh = envelope();

        await receiver.CompleteAsync(fresh);
        await receiver.CompleteAsync(alreadyHandled);
        await receiver.CompleteAsync(alsoFresh);

        await receiver.DrainAsync();

        await theRuntime.Storage.Inbox.Received(1)
            .MarkIncomingEnvelopeAsHandledAsync(Arg.Is<IReadOnlyList<Envelope>>(list =>
                list.Count == 2 && list.Contains(fresh) && list.Contains(alsoFresh) && !list.Contains(alreadyHandled)));
    }

    [Fact]
    public async Task a_failed_batch_falls_back_to_marking_one_at_a_time()
    {
        var receiver = buildReceiver();
        var envelopes = Enumerable.Range(0, 4).Select(_ => envelope()).ToArray();

        theRuntime.Storage.Inbox.MarkIncomingEnvelopeAsHandledAsync(Arg.Any<IReadOnlyList<Envelope>>())
            .Throws(new InvalidOperationException("the batch UPDATE blew up"));

        foreach (var e in envelopes)
        {
            await receiver.CompleteAsync(e);
        }

        await receiver.DrainAsync();

        // Nothing is lost: every envelope is retried individually through the per-envelope block
        foreach (var e in envelopes)
        {
            await theRuntime.Storage.Inbox.Received(1).MarkIncomingEnvelopeAsHandledAsync(e);
        }
    }

    [Fact]
    public async Task a_batch_size_of_one_opts_out_of_batching()
    {
        var receiver = buildReceiver(batchSize: 1);
        var envelopes = Enumerable.Range(0, 3).Select(_ => envelope()).ToArray();

        foreach (var e in envelopes)
        {
            await receiver.CompleteAsync(e);
        }

        await receiver.DrainAsync();

        // The escape hatch: one UPDATE per completion, as before
        foreach (var e in envelopes)
        {
            await theRuntime.Storage.Inbox.Received(1).MarkIncomingEnvelopeAsHandledAsync(e);
        }

        await theRuntime.Storage.Inbox.DidNotReceive().MarkIncomingEnvelopeAsHandledAsync(Arg.Any<IReadOnlyList<Envelope>>());
    }

    [Fact]
    public async Task batch_message_children_are_marked_handled_together()
    {
        var receiver = buildReceiver();
        var children = Enumerable.Range(0, 4).Select(_ => envelope()).ToArray();
        var batch = new Envelope(new object[] { "a", "b", "c", "d" }, children);

        await receiver.CompleteAsync(batch);
        await receiver.DrainAsync();

        await theRuntime.Storage.Inbox.Received(1)
            .MarkIncomingEnvelopeAsHandledAsync(Arg.Is<IReadOnlyList<Envelope>>(list =>
                list.Count == 4 && children.All(list.Contains)));
    }

    [Fact]
    public void the_window_matches_the_insert_side()
    {
        DurableReceiver.MarkAsHandledBatchWindow.ShouldBe(5.Milliseconds());
    }
}
