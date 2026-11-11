using System.Collections.Concurrent;
using System.Diagnostics;
using System.Reflection;
using JasperFx.Core;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Shouldly;
using Wolverine;
using Wolverine.ErrorHandling;
using Wolverine.Logging;
using Wolverine.Tracking;
using Wolverine.Persistence.Durability;
using Wolverine.Runtime;
using Wolverine.Runtime.WorkerQueues;
using Wolverine.Transports;
using Wolverine.Transports.Local;
using Wolverine.Util;
using Xunit;

namespace CoreTests.Transports.Local;

/// <summary>
/// What a durable local queue does with work that reaches it while it is paused, and how long a pause raised from
/// inside its own handler pipeline really lasts. Its receiver is gone for the length of a pause, and every way in
/// used to assume it was there.
/// </summary>
public class paused_durable_local_queue
{
    private const string QueueName = "paused-durable";

    /// <summary>
    /// A real store in every respect but the one call under test. Re-listing IMessageStore re-maps the interface to
    /// the <c>new</c> method below.
    /// </summary>
    private class StoreThatRecordsReleases : NullMessageStore, IMessageStore
    {
        public readonly ConcurrentBag<(int OwnerId, Guid Id)> Released = new();

        public new Task ReassignIncomingAsync(int ownerId, IReadOnlyList<Envelope> incoming)
        {
            foreach (var envelope in incoming) Released.Add((ownerId, envelope.Id));
            return Task.CompletedTask;
        }
    }

    private static async Task<IHost> hostWith(IMessageStore? store = null, Action<WolverineOptions>? configure = null)
    {
        return await Host.CreateDefaultBuilder()
            .UseWolverine(opts =>
            {
                opts.ApplicationAssembly = typeof(paused_durable_local_queue).Assembly;
                opts.Services.AddSingleton(store ?? new NullMessageStore());
                opts.Policies.UseDurableLocalQueues();
                opts.PublishMessage<PausedQueueMessage>().ToLocalQueue(QueueName);
                configure?.Invoke(opts);
            }).StartAsync();
    }

    private static DurableLocalQueue queueIn(IHost host)
    {
        var runtime = host.GetRuntime();
        return runtime.Endpoints.AgentForLocalQueue(QueueName).ShouldBeOfType<DurableLocalQueue>();
    }

    private static Envelope envelopeFor(DurableLocalQueue queue, string name)
    {
        return new Envelope(new PausedQueueMessage(name))
        {
            Destination = queue.Uri,
            MessageType = typeof(PausedQueueMessage).ToMessageTypeName(),
            ContentType = EnvelopeConstants.JsonContentType,
            Status = EnvelopeStatus.Incoming
        };
    }

    /// <summary>
    /// F17. The scheduled poller has already claimed a due row as Incoming and owned by this node when it hands the
    /// envelope over, and the hand-over is the only thing that can give it back (GH-4822). A paused durable local
    /// queue returned without a word, so the row stayed owned by a live node and the message never ran.
    /// </summary>
    [Fact]
    public async Task a_promoted_envelope_for_a_paused_queue_is_released_back_to_any_node()
    {
        var store = new StoreThatRecordsReleases();
        using var host = await hostWith(store);
        var queue = queueIn(host);

        await queue.PauseAsync(1.Minutes());

        var envelope = envelopeFor(queue, "due while paused");
        await Should.ThrowAsync<InvalidOperationException>(() =>
            ((IListenerCircuit)queue).EnqueueDirectlyAsync([envelope]));

        await host.GetRuntime().EnqueueDirectlyAsync([envelope]);

        store.Released.ShouldContain((TransportConstants.AnyNode, envelope.Id));
    }

    /// <summary>
    /// F6. A batch carrier assembled while its batch queue is paused was handed to a receiver that was not there: a
    /// NullReferenceException in the batching processor, the carrier gone, and its members stranded on a live node.
    /// The queue holds what reaches it while paused and runs it when it resumes.
    /// </summary>
    [Fact]
    public async Task an_envelope_enqueued_while_paused_runs_when_the_queue_resumes()
    {
        PausedQueueHandler.Reset();
        using var host = await hostWith();
        var queue = queueIn(host);

        await queue.PauseAsync(1.Minutes());

        await ((ILocalReceiver)queue).EnqueueAsync(envelopeFor(queue, "async"));
#pragma warning disable VSTHRD103 // the synchronous overload is the one under test
        ((ILocalReceiver)queue).Enqueue(envelopeFor(queue, "sync"));
#pragma warning restore VSTHRD103

        await Task.Delay(250.Milliseconds(), TestContext.Current.CancellationToken);
        PausedQueueHandler.Handled.ShouldBeEmpty("nothing runs on a paused queue");

        await ((IListenerCircuit)queue).StartAsync();

        await PausedQueueHandler.WaitForAsync("async", "sync");
    }

    /// <summary>
    /// L1. A pause raised by an error policy runs inside the handler pipeline of the very message that failed. The
    /// durable local queue latched and then waited for the queue to drain, which included that frame, so a one
    /// second pause lasted DrainTimeout plus one second while the failing handler's slot was held.
    /// </summary>
    [Fact]
    public async Task a_pause_raised_from_inside_the_pipeline_does_not_wait_on_its_own_execution()
    {
        PausedQueueHandler.Reset();
        using var host = await hostWith(configure: opts =>
        {
            opts.Durability.DrainTimeout = 10.Seconds();
            opts.OnException<PauseTheQueue>().Requeue().AndPauseProcessing(1.Seconds());
        });

        var queue = queueIn(host);
        var circuit = (IListenerCircuit)queue;

        var stopwatch = Stopwatch.StartNew();
        await host.MessageBus().PublishAsync(new PausedQueueMessage("pause"));

        await waitForStatusAsync(circuit, ListeningStatus.Paused, 20.Seconds());
        await waitForStatusAsync(circuit, ListeningStatus.Accepting, 20.Seconds());
        stopwatch.Stop();

        stopwatch.Elapsed.ShouldBeLessThan(6.Seconds(),
            "a one second pause must not sit out the ten second DrainTimeout waiting for itself");

        // And the resumed queue really works
        await host.MessageBus().PublishAsync(new PausedQueueMessage("after"));
        await PausedQueueHandler.WaitForAsync("after");
    }

    /// <summary>
    /// A start on a queue that was already accepting built a new receiver and swapped it in, and nothing disposed the
    /// one it replaced.
    /// </summary>
    [Fact]
    public async Task starting_a_queue_that_is_already_accepting_keeps_its_receiver_5348()
    {
        using var host = await hostWith();
        var queue = queueIn(host);
        var receiver = receiverOf(queue);

        await ((IListenerCircuit)queue).StartAsync();

        ((IListenerCircuit)queue).Status.ShouldBe(ListeningStatus.Accepting);
        receiverOf(queue).ShouldBeSameAs(receiver);
    }

    /// <summary>
    /// Two starts can overlap: the pause's Restarter, a global partition slot coming back, and anything that resumes
    /// the queue on its Paused event all call StartAsync. A handler still running keeps the paused drain, and so the
    /// first start, waiting while the second arrives. Each start built and published a receiver of its own.
    /// </summary>
    [Fact]
    public async Task overlapping_starts_build_one_receiver_5348()
    {
        HeldQueueHandler.Reset();
        using var host = await hostWith(configure: opts =>
            opts.PublishMessage<HeldQueueMessage>().ToLocalQueue(QueueName));
        var queue = queueIn(host);
        var circuit = (IListenerCircuit)queue;

        await host.MessageBus().PublishAsync(new HeldQueueMessage());
        await HeldQueueHandler.Entered.Task.WaitAsync(10.Seconds(), TestContext.Current.CancellationToken);

        var states = new ListenerStatesInOrder(queue.Uri);
        using var subscription = host.GetRuntime().Tracker.Subscribe(states);

        await queue.PauseAsync(1.Minutes());

        var first = circuit.StartAsync().AsTask();
        var second = circuit.StartAsync().AsTask();

        HeldQueueHandler.Release.SetResult();
        await Task.WhenAll(first, second).WaitAsync(10.Seconds(), TestContext.Current.CancellationToken);

        await states.WaitForEverythingPublishedAsync(host);

        circuit.Status.ShouldBe(ListeningStatus.Accepting);
        states.AcceptingAfterPaused.ShouldBe(1);
    }

    /// <summary>
    /// The circuit breaker's pause waits for its drain between taking the receiver and installing its Restarter. A
    /// start in that wait resumed the queue, and the pause then published Paused and armed a Restarter on a queue
    /// that was accepting, so the last state anyone saw was Paused.
    /// </summary>
    [Fact]
    public async Task a_start_during_a_pauses_drain_is_not_followed_by_its_paused_event_5348()
    {
        HeldQueueHandler.Reset();
        using var host = await hostWith(configure: opts =>
            opts.PublishMessage<HeldQueueMessage>().ToLocalQueue(QueueName));
        var queue = queueIn(host);
        var circuit = (IListenerCircuit)queue;

        await host.MessageBus().PublishAsync(new HeldQueueMessage());
        await HeldQueueHandler.Entered.Task.WaitAsync(10.Seconds(), TestContext.Current.CancellationToken);

        var states = new ListenerStatesInOrder(queue.Uri);
        using var subscription = host.GetRuntime().Tracker.Subscribe(states);

        var pause = queue.PauseWithDrainAsync(1.Minutes()).AsTask();
        circuit.Status.ShouldBe(ListeningStatus.Paused);

        await circuit.StartAsync().AsTask().WaitAsync(10.Seconds(), TestContext.Current.CancellationToken);
        pause.IsCompleted.ShouldBeFalse("the held handler keeps the pause waiting for its drain");

        HeldQueueHandler.Release.SetResult();
        await pause.WaitAsync(10.Seconds(), TestContext.Current.CancellationToken);

        await states.WaitForEverythingPublishedAsync(host);

        circuit.Status.ShouldBe(ListeningStatus.Accepting);
        states.Statuses.ShouldNotContain(ListeningStatus.Paused);
        restarterOf(queue).ShouldBeNull();
    }

    /// <summary>
    /// The pause published Paused before it installed its Restarter. Something that resumes the queue on that event
    /// could finish first, so the resume disposed no Restarter, and the one installed after it fired on an accepting
    /// queue and built a second receiver. The gate holds the pause at its log line, after the publish, until the
    /// resume is over: the order the race needs, every time.
    /// </summary>
    [Fact]
    public async Task a_resume_on_the_paused_event_is_not_followed_by_the_pauses_restart_5348()
    {
        var resumed = new TaskCompletionSource<DurableReceiver>(TaskCreationOptions.RunContinuationsAsynchronously);
        var holdThePause = new HoldPauseUntilResumed(resumed.Task);
        using var host = await hostWith(configure: opts => opts.Services.AddSingleton<ILoggerProvider>(holdThePause));
        var queue = queueIn(host);
        holdThePause.QueueUri = queue.Uri;

        using var subscription = host.GetRuntime().Tracker.Subscribe(new ResumeOnPaused(queue, resumed));

        await queue.PauseAsync(250.Milliseconds());
        var receiverAfterResume = await resumed.Task.WaitAsync(10.Seconds(), TestContext.Current.CancellationToken);

        // Well past the pause time, so its Restarter has had every chance to fire
        await Task.Delay(1.Seconds(), TestContext.Current.CancellationToken);

        holdThePause.ResumeFinishedDuringTheHold.ShouldBeTrue(
            "the race needs the resume to finish while the pause is held after publishing Paused");
        ((IListenerCircuit)queue).Status.ShouldBe(ListeningStatus.Accepting);
        receiverOf(queue).ShouldBeSameAs(receiverAfterResume);
        restarterOf(queue).ShouldBeNull();
    }

    /// <summary>
    /// Dispose disposed the receiver twice and never the pause's Restarter, which then started the disposed queue
    /// again.
    /// </summary>
    [Fact]
    public async Task disposing_a_paused_queue_cancels_its_restart_5348()
    {
        using var host = await hostWith();
        var queue = queueIn(host);

        await queue.PauseAsync(1.Seconds());
        queue.Dispose();

        await Task.Delay(2.Seconds(), TestContext.Current.CancellationToken);

        ((IListenerCircuit)queue).Status.ShouldBe(ListeningStatus.Paused);
        receiverOf(queue).ShouldBeNull();
    }

    private static DurableReceiver? receiverOf(DurableLocalQueue queue)
    {
        var field = typeof(DurableLocalQueue).GetField("_receiver", BindingFlags.NonPublic | BindingFlags.Instance)!;
        return (DurableReceiver?)field.GetValue(queue);
    }

    private static object? restarterOf(DurableLocalQueue queue)
    {
        var field = typeof(DurableLocalQueue).GetField("_restarter", BindingFlags.NonPublic | BindingFlags.Instance)!;
        return field.GetValue(queue);
    }

    private class ResumeOnPaused(DurableLocalQueue queue, TaskCompletionSource<DurableReceiver> resumed)
        : IObserver<IWolverineEvent>
    {
        private int _resumes;

        public void OnNext(IWolverineEvent value)
        {
            if (value is not ListenerState { Status: ListeningStatus.Paused } state || state.Uri != queue.Uri) return;
            if (Interlocked.Increment(ref _resumes) > 1) return;

            _ = Task.Run(async () =>
            {
                try
                {
                    await ((IListenerCircuit)queue).StartAsync();
                    resumed.TrySetResult(receiverOf(queue)!);
                }
                catch (Exception e)
                {
                    resumed.TrySetException(e);
                }
            });
        }

        public void OnCompleted()
        {
        }

        public void OnError(Exception error)
        {
        }
    }

    private class ListenerStatesInOrder(Uri queueUri) : IObserver<IWolverineEvent>
    {
        private static readonly Uri SentinelUri = new("local://sentinel");

        private readonly TaskCompletionSource _sentinel = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public ConcurrentQueue<ListeningStatus> Statuses { get; } = new();

        // The host's own start publishes Accepting too, and may reach a new subscriber late
        public int AcceptingAfterPaused =>
            Statuses.SkipWhile(x => x != ListeningStatus.Paused).Count(x => x == ListeningStatus.Accepting);

        /// <summary>
        /// The Tracker delivers to subscribers in order on a block of its own, so once a sentinel published now
        /// arrives, so has every state published before it.
        /// </summary>
        public async Task WaitForEverythingPublishedAsync(IHost host)
        {
            host.GetRuntime().Tracker.Publish(new ListenerState(SentinelUri, "sentinel", ListeningStatus.Stopped));
            await _sentinel.Task.WaitAsync(10.Seconds(), TestContext.Current.CancellationToken);
        }

        public void OnNext(IWolverineEvent value)
        {
            if (value is not ListenerState state) return;

            if (state.Uri == SentinelUri) _sentinel.TrySetResult();
            else if (state.Uri == queueUri) Statuses.Enqueue(state.Status);
        }

        public void OnCompleted()
        {
        }

        public void OnError(Exception error)
        {
        }
    }

    private class HoldPauseUntilResumed(Task resumed) : ILoggerProvider, ILogger
    {
        public Uri? QueueUri { get; set; }

        public bool ResumeFinishedDuringTheHold { get; private set; }

        public ILogger CreateLogger(string categoryName) => this;

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            if (QueueUri == null || formatter(state, exception) != $"Pausing message listening at {QueueUri}") return;

            // Bounded, so a pause that logs somewhere the resume cannot get past still finishes. A failed resume
            // fails the test through the task itself, not here.
#pragma warning disable VSTHRD002 // ILogger.Log is synchronous, and holding the pause's own thread is the point
            Task.WhenAny(resumed, Task.Delay(5.Seconds())).Wait();
#pragma warning restore VSTHRD002
            ResumeFinishedDuringTheHold = resumed.IsCompleted;
        }

        public void Dispose()
        {
        }
    }

    private static async Task waitForStatusAsync(IListenerCircuit circuit, ListeningStatus status, TimeSpan timeout)
    {
        var deadline = DateTimeOffset.UtcNow.Add(timeout);
        while (DateTimeOffset.UtcNow < deadline)
        {
            if (circuit.Status == status) return;
            await Task.Delay(25.Milliseconds());
        }

        throw new TimeoutException($"The queue never reached {status}; it is {circuit.Status}");
    }
}

public record PausedQueueMessage(string Name);

public class PauseTheQueue(string message) : Exception(message);

public record HeldQueueMessage;

public static class HeldQueueHandler
{
    public static TaskCompletionSource Entered { get; private set; } =
        new(TaskCreationOptions.RunContinuationsAsynchronously);

    public static TaskCompletionSource Release { get; private set; } =
        new(TaskCreationOptions.RunContinuationsAsynchronously);

    public static void Reset()
    {
        Entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        Release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
    }

    public static async Task Handle(HeldQueueMessage message)
    {
        Entered.TrySetResult();
        await Release.Task;
    }
}

public static class PausedQueueHandler
{
    public static readonly ConcurrentQueue<string> Handled = new();

    public static void Reset()
    {
        Handled.Clear();
    }

    public static void Handle(PausedQueueMessage message, Envelope envelope)
    {
        if (message.Name == "pause" && envelope.Attempts <= 1)
        {
            throw new PauseTheQueue("pause the queue");
        }

        Handled.Enqueue(message.Name);
    }

    public static async Task WaitForAsync(params string[] names)
    {
        var deadline = DateTimeOffset.UtcNow.Add(10.Seconds());
        while (DateTimeOffset.UtcNow < deadline)
        {
            if (names.All(Handled.Contains)) return;
            await Task.Delay(25.Milliseconds());
        }

        throw new TimeoutException(
            $"Expected {names.Join(", ")} to be handled; handled were {Handled.ToArray().Join(", ")}");
    }
}
