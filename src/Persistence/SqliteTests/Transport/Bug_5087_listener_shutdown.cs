using JasperFx;
using JasperFx.Core;
using Microsoft.Extensions.DependencyInjection;
using Wolverine.ComplianceTests;
using Wolverine.Runtime;
using Wolverine.Sqlite;
using Wolverine.Sqlite.Transport;
using Wolverine.Tracking;
using Wolverine.Transports;

namespace SqliteTests.Transport;

/// <summary>
/// GH-5087. <see cref="SqliteQueueListener.StopAsync"/> and <c>DisposeAsync</c> cancel the listener's token
/// and then called <c>SafeDispose()</c> on the dequeue and scheduled-promotion tasks, which joins neither of
/// them, so a "stopped" listener could still be handing envelopes to its receiver. Looking into that turned
/// up the worse half: <see cref="IListener.DeferAsync"/> sent with the token StopAsync had just cancelled, so
/// every envelope a shutdown drain tried to hand back was lost from a buffered queue.
/// </summary>
[Collection("Bug_5087_listener_shutdown")]
public class Bug_5087_listener_shutdown
{
    private const string QueueName = "bug_5087";

    [Fact]
    public async Task stop_does_not_report_a_complete_stop_while_the_dequeue_worker_is_still_running()
    {
        using var database = Servers.CreateDatabase(nameof(Bug_5087_listener_shutdown));
        var host = await startPublishOnlyHostAsync(database.ConnectionString);

        var receiver = new GatedReceiver();

        try
        {
            var queue = queueFor(host);
            await queue.SendAsync(ObjectMother.Envelope());

            await using var listener = new SqliteQueueListener(queue, host.GetRuntime(), receiver,
                queue.DataSource, null);
            await listener.StartAsync();

            // The dequeue worker is now parked inside the receiver
            await receiver.Entered.Task.WaitAsync(30.Seconds(), TestContext.Current.CancellationToken);

            var stop = listener.StopAsync().AsTask();
            await Task.WhenAny(stop, Task.Delay(1.Seconds(), TestContext.Current.CancellationToken));

            stop.IsCompleted.ShouldBeFalse(
                "StopAsync returned while the dequeue worker was still inside IReceiver.ReceivedAsync");

            receiver.Release();
            await stop.WaitAsync(30.Seconds(), TestContext.Current.CancellationToken);
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
        using var database = Servers.CreateDatabase(nameof(Bug_5087_listener_shutdown));
        var host = await startPublishOnlyHostAsync(database.ConnectionString);

        try
        {
            var queue = queueFor(host);
            queue.PollingInterval = 30.Seconds();

            await using var listener = new SqliteQueueListener(queue, host.GetRuntime(), new GatedReceiver(),
                queue.DataSource, null);
            await listener.StartAsync();

            // Both workers have polled the empty queue once and are now asleep for the polling interval
            await Task.Delay(1.Seconds(), TestContext.Current.CancellationToken);

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

    [Fact]
    public async Task no_envelope_reaches_the_receiver_after_stop_returns()
    {
        using var database = Servers.CreateDatabase(nameof(Bug_5087_listener_shutdown));
        var host = await startPublishOnlyHostAsync(database.ConnectionString);

        var receiver = new GatedReceiver();

        try
        {
            var queue = queueFor(host);

            // One batch of three: the worker hands them to the receiver one at a time in a foreach
            for (var i = 0; i < 3; i++)
            {
                await queue.SendAsync(ObjectMother.Envelope());
            }

            await using var listener = new SqliteQueueListener(queue, host.GetRuntime(), receiver,
                queue.DataSource, null);
            await listener.StartAsync();

            await receiver.Entered.Task.WaitAsync(30.Seconds(), TestContext.Current.CancellationToken);

            // Released on a timer so that a StopAsync that does join the worker cannot deadlock the test
            _ = Task.Run(async () =>
            {
                await Task.Delay(500.Milliseconds(), TestContext.Current.CancellationToken);
                receiver.Release();
            }, TestContext.Current.CancellationToken);

            await listener.StopAsync();
            receiver.StopReturned = true;

            // Give a still-running worker every chance to finish its batch
            await Task.Delay(2.Seconds(), TestContext.Current.CancellationToken);

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
    /// which for this listener is a send with the token StopAsync just cancelled.
    /// </summary>
    [Fact]
    public async Task an_envelope_deferred_after_stop_goes_back_on_the_queue()
    {
        using var database = Servers.CreateDatabase(nameof(Bug_5087_listener_shutdown));
        var host = await startPublishOnlyHostAsync(database.ConnectionString);

        try
        {
            var queue = queueFor(host);

            await using var listener = new SqliteQueueListener(queue, host.GetRuntime(), new GatedReceiver(),
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

    /// <summary>
    /// The end-to-end consequence of the above for a buffered SQLite queue: the dequeue is destructive, so an
    /// envelope that is still in the BufferedReceiver when the host stops only survives if the deferral back
    /// onto the queue table works.
    /// </summary>
    [Fact]
    public async Task buffered_envelopes_still_in_memory_at_shutdown_are_not_lost()
    {
        const int count = 5;

        using var database = Servers.CreateDatabase(nameof(Bug_5087_listener_shutdown));
        Bug5087Handler.Reset();

        var host = await Host.CreateDefaultBuilder()
            .UseWolverine(opts =>
            {
                opts.Durability.Mode = DurabilityMode.Solo;
                opts.Discovery.DisableConventionalDiscovery().IncludeType(typeof(Bug5087Handler));
                opts.UseSqlitePersistenceAndTransport(database.ConnectionString).AutoProvision();

                opts.ListenToSqliteQueue(QueueName)
                    .PollingInterval(100.Milliseconds())
                    .BufferedInMemory()
                    .Sequential();
            }).StartAsync(TestContext.Current.CancellationToken);

        var stopped = false;

        try
        {
            var queue = queueFor(host);
            var endpoint = host.MessageBus().EndpointFor($"sqlite://{QueueName}".ToUri());

            for (var i = 0; i < count; i++)
            {
                await endpoint.SendAsync(new Bug5087Message(i));
            }

            // The first one is parked in the handler, the other four are queued behind it in memory
            await Bug5087Handler.Started.Task.WaitAsync(30.Seconds(), TestContext.Current.CancellationToken);

            var stop = host.StopAsync(TestContext.Current.CancellationToken);
            await Task.Delay(500.Milliseconds(), TestContext.Current.CancellationToken);
            Bug5087Handler.Release();
            await stop.WaitAsync(60.Seconds(), TestContext.Current.CancellationToken);
            stopped = true;

            var remaining = await queue.CountAsync();

            (Bug5087Handler.Handled + remaining).ShouldBe(count,
                $"handled {Bug5087Handler.Handled}, {remaining} back on the queue: the rest were dequeued and dropped");
        }
        finally
        {
            Bug5087Handler.Release();
            if (!stopped) await host.StopAsync(TestContext.Current.CancellationToken);
            host.Dispose();
        }
    }

    private static Task<IHost> startPublishOnlyHostAsync(string connectionString)
    {
        return Host.CreateDefaultBuilder()
            .UseWolverine(opts =>
            {
                opts.Durability.Mode = DurabilityMode.Solo;
                opts.Durability.ScheduledJobFirstExecution = 0.Seconds();
                opts.Discovery.DisableConventionalDiscovery();
                opts.UseSqlitePersistenceAndTransport(connectionString).AutoProvision();
                opts.PublishAllMessages().ToSqliteQueue(QueueName);
            }).StartAsync();
    }

    private static SqliteQueue queueFor(IHost host)
    {
        return host.GetRuntime().Options.Transports.GetOrCreate<SqliteTransport>().Queues[QueueName];
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

        public TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public volatile bool StopReturned;

        public int DeliveredAfterStop => _deliveredAfterStop;

        public void Release() => _gate.TrySetResult();

        public void Dispose()
        {
        }

        public ValueTask ReceivedAsync(IListener listener, Envelope[] messages)
        {
            throw new NotSupportedException();
        }

        public async ValueTask ReceivedAsync(IListener listener, Envelope envelope)
        {
            if (StopReturned)
            {
                Interlocked.Increment(ref _deliveredAfterStop);
            }

            Entered.TrySetResult();
            await _gate.Task;
        }

        public ValueTask DrainAsync() => ValueTask.CompletedTask;

        public IHandlerPipeline Pipeline => null!;
    }
}

public record Bug5087Message(int Number);

public static class Bug5087Handler
{
    private static TaskCompletionSource _gate = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private static int _handled;

    public static TaskCompletionSource Started { get; private set; } =
        new(TaskCreationOptions.RunContinuationsAsynchronously);

    public static int Handled => _handled;

    public static void Reset()
    {
        _gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        Started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        _handled = 0;
    }

    public static void Release() => _gate.TrySetResult();

    public static async Task Handle(Bug5087Message message)
    {
        Started.TrySetResult();
        await _gate.Task;
        Interlocked.Increment(ref _handled);
    }
}
