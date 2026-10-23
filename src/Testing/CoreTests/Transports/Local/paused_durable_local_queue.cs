using System.Collections.Concurrent;
using System.Diagnostics;
using JasperFx.Core;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Shouldly;
using Wolverine;
using Wolverine.ErrorHandling;
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
