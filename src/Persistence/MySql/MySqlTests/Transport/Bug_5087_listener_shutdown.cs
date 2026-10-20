using IntegrationTests;
using JasperFx;
using JasperFx.Core;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using MySqlConnector;
using Shouldly;
using Wolverine;
using Wolverine.ComplianceTests;
using Wolverine.MySql;
using Wolverine.MySql.Transport;
using Wolverine.Runtime;
using Wolverine.Tracking;
using Wolverine.Transports;

namespace MySqlTests.Transport;

/// <summary>
/// The MySQL leg of GH-5087. <see cref="MySqlQueueListener.StopAsync"/> and <c>DisposeAsync</c> cancelled the
/// listener's token and then called <c>SafeDispose()</c> on the dequeue and scheduled-promotion tasks, which
/// joins neither of them, so a "stopped" listener could still be handing envelopes to its receiver.
/// </summary>
[Collection("mysql")]
public class Bug_5087_listener_shutdown
{
    private const string SchemaName = "wolverine_gh5087";
    private const string QueueName = "bug_5087";

    private static CancellationToken ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task stop_does_not_report_a_complete_stop_while_the_dequeue_worker_is_still_running()
    {
        var host = await startPublishOnlyHostAsync();
        var receiver = new GatedReceiver();

        try
        {
            var queue = await purgedQueueAsync(host);
            await queue.SendAsync(ObjectMother.Envelope());

            await using var listener = new MySqlQueueListener(queue, host.GetRuntime(), receiver,
                queue.DataSource, null);
            await listener.StartAsync();

            // The dequeue worker is now parked inside the receiver
            await receiver.Entered.Task.WaitAsync(30.Seconds(), ct);

            var stop = listener.StopAsync().AsTask();
            await Task.WhenAny(stop, Task.Delay(1.Seconds(), ct));

            stop.IsCompleted.ShouldBeFalse(
                "StopAsync returned while the dequeue worker was still inside IReceiver.ReceivedAsync");

            receiver.Release();
            await stop.WaitAsync(30.Seconds(), ct);
        }
        finally
        {
            receiver.Release();
            await stopHostAsync(host);
        }
    }

    /// <summary>
    /// The other half of joining the workers: StopAsync waits for them now, so every pause they take has to
    /// end on the stop token, or stopping an idle listener would wait out its polling interval (GH-4952 is
    /// the same defect in the control listener).
    /// </summary>
    [Fact]
    public async Task an_idle_listener_stops_without_waiting_out_its_polling_interval()
    {
        var host = await startPublishOnlyHostAsync();

        try
        {
            var queue = await purgedQueueAsync(host);
            queue.PollingInterval = 30.Seconds();

            await using var listener = new MySqlQueueListener(queue, host.GetRuntime(), new GatedReceiver(),
                queue.DataSource, null);
            await listener.StartAsync();

            // Both workers have polled the empty queue once and are now asleep for the polling interval
            await Task.Delay(1.Seconds(), ct);

            var stopwatch = System.Diagnostics.Stopwatch.StartNew();
            await listener.StopAsync();
            stopwatch.Stop();

            stopwatch.Elapsed.ShouldBeLessThan(5.Seconds());
        }
        finally
        {
            await stopHostAsync(host);
        }
    }

    /// <summary>
    /// The MySQL listener hands a dequeued batch to the receiver in one call, so the way an envelope reaches the
    /// receiver after a stop is the batch that was in flight when StopAsync returned.
    /// </summary>
    [Fact]
    public async Task no_envelope_reaches_the_receiver_after_stop_returns()
    {
        var host = await startPublishOnlyHostAsync();
        var receiver = new GatedReceiver();

        try
        {
            var queue = await purgedQueueAsync(host);

            // One batch of three
            for (var i = 0; i < 3; i++)
            {
                await queue.SendAsync(ObjectMother.Envelope());
            }

            await using var listener = new MySqlQueueListener(queue, host.GetRuntime(), receiver,
                queue.DataSource, null);
            await listener.StartAsync();

            await receiver.Entered.Task.WaitAsync(30.Seconds(), ct);

            // Released on a timer so that a StopAsync that does join the worker cannot deadlock the test
            _ = Task.Run(async () =>
            {
                await Task.Delay(500.Milliseconds(), ct);
                receiver.Release();
            }, ct);

            await listener.StopAsync();
            receiver.StopReturned = true;
            var inFlightWhenStopReturned = receiver.InFlight;

            // Give a still-running worker every chance to finish its batch
            await Task.Delay(2.Seconds(), ct);

            inFlightWhenStopReturned.ShouldBe(0,
                "StopAsync returned while the receiver was still working through the dequeued batch");
            receiver.DeliveredAfterStop.ShouldBe(0,
                "the dequeue worker kept handing envelopes to the receiver after StopAsync had returned");
        }
        finally
        {
            receiver.Release();
            await stopHostAsync(host);
        }
    }

    /// <summary>
    /// ListeningAgent.StopAndDrainCoreAsync stops the listener FIRST, then latches and drains the receiver.
    /// A latched receiver hands every envelope it still holds back through <see cref="IListener.DeferAsync"/>,
    /// which for this listener was a send with the token StopAsync just cancelled.
    /// </summary>
    [Fact]
    public async Task an_envelope_deferred_after_stop_goes_back_on_the_queue()
    {
        var host = await startPublishOnlyHostAsync();

        try
        {
            var queue = await purgedQueueAsync(host);

            await using var listener = new MySqlQueueListener(queue, host.GetRuntime(), new GatedReceiver(),
                queue.DataSource, null);

            await listener.StopAsync();

            var failure = await Record.ExceptionAsync(async () => await listener.DeferAsync(ObjectMother.Envelope()));
            failure.ShouldBeNull("DeferAsync sends with the token StopAsync cancelled");

            (await queue.CountAsync()).ShouldBe(1);
        }
        finally
        {
            await stopHostAsync(host);
        }
    }

    private static async Task<IHost> startPublishOnlyHostAsync()
    {
        await using (var conn = new MySqlConnection(Servers.MySqlConnectionString))
        {
            await conn.OpenAsync(ct);
            await using var cmd = conn.CreateCommand();
            cmd.CommandText = $"CREATE DATABASE IF NOT EXISTS {SchemaName}";
            await cmd.ExecuteNonQueryAsync(ct);
        }

        return await Host.CreateDefaultBuilder()
            .UseWolverine(opts =>
            {
                opts.Durability.Mode = DurabilityMode.Solo;
                opts.Durability.ScheduledJobFirstExecution = 0.Seconds();
                opts.Discovery.DisableConventionalDiscovery();
                opts.UseMySqlPersistenceAndTransport(Servers.MySqlConnectionString, schema: SchemaName,
                    transportSchema: SchemaName);

                // Declare the queue without listening to it: the test drives its own listener
                opts.PublishAllMessages().ToMySqlQueue(QueueName);
            }).StartAsync(ct);
    }

    // The queue table outlives the host, so clear whatever an earlier test left behind
    private static async Task<MySqlQueue> purgedQueueAsync(IHost host)
    {
        var queue = host.GetRuntime().Options.Transports.GetOrCreate<MySqlTransport>().Queues[QueueName];
        await queue.PurgeAsync(NullLogger.Instance);
        (await queue.CountAsync()).ShouldBe(0);
        return queue;
    }

    private static async Task stopHostAsync(IHost host)
    {
        await host.StopAsync();
        host.Dispose();
    }

    private class GatedReceiver : IReceiver
    {
        private readonly TaskCompletionSource _gate = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int _deliveredAfterStop;
        private int _inFlight;

        public TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public volatile bool StopReturned;

        public int DeliveredAfterStop => _deliveredAfterStop;

        public int InFlight => Volatile.Read(ref _inFlight);

        public void Release() => _gate.TrySetResult();

        public void Dispose()
        {
        }

        public ValueTask ReceivedAsync(IListener listener, Envelope[] messages) => receiveAsync(messages.Length);

        public ValueTask ReceivedAsync(IListener listener, Envelope envelope) => receiveAsync(1);

        private async ValueTask receiveAsync(int count)
        {
            if (StopReturned)
            {
                Interlocked.Add(ref _deliveredAfterStop, count);
            }

            Interlocked.Increment(ref _inFlight);
            try
            {
                Entered.TrySetResult();
                await _gate.Task;
            }
            finally
            {
                Interlocked.Decrement(ref _inFlight);
            }
        }

        public ValueTask DrainAsync() => ValueTask.CompletedTask;

        public IHandlerPipeline Pipeline => null!;
    }
}
