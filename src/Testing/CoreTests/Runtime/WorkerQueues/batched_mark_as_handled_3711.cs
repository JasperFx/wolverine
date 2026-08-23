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
/// completion; concurrent completions now share one IReadOnlyList flush -- while CompleteAsync still
/// does not return until the envelope's UPDATE has landed.
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

    /// <summary>
    /// Hold the FIRST inbox call open so every completion posted meanwhile piles up behind it, then
    /// release it. That is the only way to make "batches form from concurrency" deterministic.
    /// </summary>
    private (TaskCompletionSource gate, TaskCompletionSource firstCallStarted) gateTheFirstInboxCall()
    {
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var first = true;

        theRuntime.Storage.Inbox.MarkIncomingEnvelopeAsHandledAsync(Arg.Any<Envelope>())
            .Returns(_ => hold());
        theRuntime.Storage.Inbox.MarkIncomingEnvelopeAsHandledAsync(Arg.Any<IReadOnlyList<Envelope>>())
            .Returns(_ => hold());

        Task hold()
        {
            if (first)
            {
                first = false;
                started.TrySetResult();
                return gate.Task;
            }

            return Task.CompletedTask;
        }

        return (gate, started);
    }

    public async ValueTask DisposeAsync()
    {
        if (theReceiver != null)
        {
            await theReceiver.DisposeAsync();
        }
    }

    [Fact]
    public async Task completions_that_arrive_during_a_flush_share_the_next_flush()
    {
        var receiver = buildReceiver();
        var (gate, firstCallStarted) = gateTheFirstInboxCall();

        var first = envelope();
        var firstCompletion = receiver.CompleteAsync(first).AsTask();
        await firstCallStarted.Task;

        // These arrive while the first UPDATE is "in flight"
        var others = Enumerable.Range(0, 5).Select(_ => envelope()).ToArray();
        var otherCompletions = others.Select(e => receiver.CompleteAsync(e).AsTask()).ToArray();

        // Nothing has been released yet -- none of them is "handled"
        firstCompletion.IsCompleted.ShouldBeFalse();
        otherCompletions.Any(x => x.IsCompleted).ShouldBeFalse();

        gate.SetResult();
        await Task.WhenAll(otherCompletions.Append(firstCompletion)).WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);

        // The lone first completion took the per-envelope path; the five that piled up went as ONE batch
        await theRuntime.Storage.Inbox.Received(1).MarkIncomingEnvelopeAsHandledAsync(first);
        await theRuntime.Storage.Inbox.Received(1)
            .MarkIncomingEnvelopeAsHandledAsync(Arg.Is<IReadOnlyList<Envelope>>(list =>
                list.Count == 5 && others.All(list.Contains)));
    }

    [Fact]
    public async Task complete_async_does_not_return_until_the_update_has_landed()
    {
        var receiver = buildReceiver();
        var (gate, firstCallStarted) = gateTheFirstInboxCall();

        var completion = receiver.CompleteAsync(envelope()).AsTask();
        await firstCallStarted.Task;

        await Task.Delay(100, TestContext.Current.CancellationToken);
        completion.IsCompleted.ShouldBeFalse("CompleteAsync returned before the inbox UPDATE finished");

        gate.SetResult();
        await completion.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task a_lone_completion_is_flushed_immediately_on_the_per_envelope_path()
    {
        var receiver = buildReceiver();
        var lone = envelope();

        await receiver.CompleteAsync(lone);

        await theRuntime.Storage.Inbox.Received(1).MarkIncomingEnvelopeAsHandledAsync(lone);
        await theRuntime.Storage.Inbox.DidNotReceive().MarkIncomingEnvelopeAsHandledAsync(Arg.Any<IReadOnlyList<Envelope>>());
    }

    [Fact]
    public async Task a_pile_up_larger_than_the_batch_size_is_flushed_in_chunks()
    {
        var receiver = buildReceiver(batchSize: 3);
        var (gate, firstCallStarted) = gateTheFirstInboxCall();

        var first = receiver.CompleteAsync(envelope()).AsTask();
        await firstCallStarted.Task;

        var others = Enumerable.Range(0, 6).Select(_ => envelope()).ToArray();
        var completions = others.Select(e => receiver.CompleteAsync(e).AsTask()).ToArray();

        gate.SetResult();
        await Task.WhenAll(completions.Append(first)).WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);

        await theRuntime.Storage.Inbox.Received(2)
            .MarkIncomingEnvelopeAsHandledAsync(Arg.Is<IReadOnlyList<Envelope>>(list => list.Count == 3));
    }

    [Fact]
    public async Task envelopes_already_marked_handled_by_transactional_middleware_are_skipped()
    {
        var receiver = buildReceiver();
        var alreadyHandled = envelope();
        alreadyHandled.Status = EnvelopeStatus.Handled;

        await receiver.CompleteAsync(alreadyHandled);

        await theRuntime.Storage.Inbox.DidNotReceive().MarkIncomingEnvelopeAsHandledAsync(alreadyHandled);
        await theRuntime.Storage.Inbox.DidNotReceive().MarkIncomingEnvelopeAsHandledAsync(Arg.Any<IReadOnlyList<Envelope>>());
    }

    [Fact]
    public async Task a_failed_batch_falls_back_to_marking_one_at_a_time_and_still_releases_the_completions()
    {
        var receiver = buildReceiver();
        var (gate, firstCallStarted) = gateTheFirstInboxCall();

        theRuntime.Storage.Inbox.MarkIncomingEnvelopeAsHandledAsync(Arg.Any<IReadOnlyList<Envelope>>())
            .Throws(new InvalidOperationException("the batch UPDATE blew up"));

        var first = receiver.CompleteAsync(envelope()).AsTask();
        await firstCallStarted.Task;

        var others = Enumerable.Range(0, 4).Select(_ => envelope()).ToArray();
        var completions = others.Select(e => receiver.CompleteAsync(e).AsTask()).ToArray();

        gate.SetResult();
        await Task.WhenAll(completions.Append(first)).WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);

        // Nothing is lost: every envelope of the failed batch went through the per-envelope path
        foreach (var e in others)
        {
            await theRuntime.Storage.Inbox.Received(1).MarkIncomingEnvelopeAsHandledAsync(e);
        }
    }

    [Fact]
    public async Task a_batch_size_of_one_opts_out_of_coalescing()
    {
        var receiver = buildReceiver(batchSize: 1);
        var (gate, firstCallStarted) = gateTheFirstInboxCall();

        var first = receiver.CompleteAsync(envelope()).AsTask();
        await firstCallStarted.Task;

        var others = Enumerable.Range(0, 3).Select(_ => envelope()).ToArray();
        var completions = others.Select(e => receiver.CompleteAsync(e).AsTask()).ToArray();

        gate.SetResult();
        await Task.WhenAll(completions.Append(first)).WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);

        // The escape hatch: one UPDATE per completion, as before
        foreach (var e in others)
        {
            await theRuntime.Storage.Inbox.Received(1).MarkIncomingEnvelopeAsHandledAsync(e);
        }

        await theRuntime.Storage.Inbox.DidNotReceive().MarkIncomingEnvelopeAsHandledAsync(Arg.Any<IReadOnlyList<Envelope>>());
    }

    [Fact]
    public async Task batch_message_children_are_marked_handled_as_one_flush()
    {
        var receiver = buildReceiver();
        var children = Enumerable.Range(0, 4).Select(_ => envelope()).ToArray();
        var batch = new Envelope(new object[] { "a", "b", "c", "d" }, children);

        // No gate needed: CompleteAsync posts every child before awaiting any, so the first child starts
        // the flush loop and the other three are waiting for it by the time it looks again
        await receiver.CompleteAsync(batch);

        var calls = theRuntime.Storage.Inbox.ReceivedCalls()
            .Where(c => c.GetMethodInfo().Name == nameof(IMessageInbox.MarkIncomingEnvelopeAsHandledAsync))
            .ToList();

        // Every child was marked exactly once, across at most two calls (the first child alone, then the rest)
        var marked = calls.SelectMany(c => c.GetArguments()[0] switch
        {
            Envelope e => [e],
            IReadOnlyList<Envelope> list => list,
            _ => Array.Empty<Envelope>()
        }).ToList();

        marked.Count.ShouldBe(4);
        children.All(marked.Contains).ShouldBeTrue();
        calls.Count.ShouldBeLessThanOrEqualTo(2);
    }
}
