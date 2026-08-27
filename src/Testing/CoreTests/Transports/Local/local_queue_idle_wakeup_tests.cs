using System.Threading;
using Microsoft.Extensions.Hosting;
using Wolverine.ComplianceTests;
using Xunit;

namespace CoreTests.Transports.Local;

/// <summary>
/// GH-4167. The local-queue expression of the JasperFx <c>Block</c> inline-continuation bug, and the
/// acceptance test for adopting JasperFx 2.57.0 (jasperfx#714).
///
/// Block used to build its channel with <c>AllowSynchronousContinuations = true</c>, so once a worker
/// parked in <c>WaitToReadAsync</c> the next <c>TryWrite</c> resumed that reader on the PUBLISHER's
/// thread and the handler ran inline before <c>PublishAsync</c> returned. Measured on 2.56.0: a burst
/// of 20 published messages all executed inline, serialized on the publishing thread, and a buffered
/// local queue with MaximumParallelMessages(5) got no parallelism at all.
///
/// A buffered local queue is UNBOUNDED (GH-3287), and an unbounded channel ran continuations inline on
/// every runtime -- so this was never specific to .NET 10, contrary to the original report. What .NET 10
/// changed (dotnet/runtime#116021) was the BOUNDED case, which affects broker-backed BufferedReceivers
/// and DurableReceiver instead.
///
/// These fail against JasperFx 2.56.0 and pass from 2.57.0 onward.
/// </summary>
public class local_queue_idle_wakeup_tests
{
    private static async Task<IHost> startHostAsync()
    {
        return await Host.CreateDefaultBuilder().UseWolverine(opts =>
        {
            // Pin the application assembly rather than letting the stack-walk fallback infer it;
            // from an async host builder it can resolve to xunit.execution.dotnet and leave this
            // host with no discovered handlers. See GH-3423.
            opts.ApplicationAssembly = typeof(local_queue_idle_wakeup_tests).Assembly;
            opts.DisableConventionalDiscovery();
            opts.IncludeType<IdleWakeHandler>();

            opts.Publish(x => x
                .Message<IdleWakeMessage>()
                .ToLocalQueue("idle")
                .MaximumParallelMessages(5));

            opts.Policies.DisableConventionalLocalRouting();
        }).StartAsync(TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task post_after_idle_does_not_process_inline_on_the_publisher()
    {
        IdleWakeTracker.Reset();

        using var host = await startHostAsync();
        var bus = host.MessageBus();

        await bus.PublishAsync(new IdleWakeMessage(1));
        await IdleWakeTracker.FirstHandled.Task.WaitAsync(TimeSpan.FromSeconds(5),
            TestContext.Current.CancellationToken);

        IdleWakeTracker.Processed.ShouldBe(1);

        // Park every worker in WaitToReadAsync.
        await Task.Delay(500, TestContext.Current.CancellationToken);

        IdleWakeTracker.CaptureWave2 = 1;
        var publisherThread = Environment.CurrentManagedThreadId;

        await bus.PublishAsync(new IdleWakeMessage(2));

        IdleWakeTracker.Processed.ShouldBe(1,
            $"Publish after idle ran the handler inline on the publisher thread " +
            $"(processed={IdleWakeTracker.Processed}, publisher={publisherThread}, " +
            $"handler={IdleWakeTracker.Wave2HandlerThreadId}). " +
            "Requires JasperFx >= 2.57.0 (jasperfx#714).");
    }

    /// <summary>
    /// The second claim in GH-4167: that a burst posted after an idle period leaves envelopes
    /// stranded in the channel while every worker sits in WaitToReadAsync. Reports rather than
    /// asserts on the inline count so the two claims stay separable.
    /// </summary>
    [Fact]
    public async Task burst_after_idle_is_fully_drained()
    {
        const int wave = 20;
        IdleWakeTracker.Reset();

        using var host = await startHostAsync();
        var bus = host.MessageBus();

        await bus.PublishAsync(new IdleWakeMessage(1));
        await IdleWakeTracker.FirstHandled.Task.WaitAsync(TimeSpan.FromSeconds(5),
            TestContext.Current.CancellationToken);

        await Task.Delay(500, TestContext.Current.CancellationToken);

        IdleWakeTracker.CaptureWave2 = 1;
        IdleWakeTracker.PublisherThreadId = Environment.CurrentManagedThreadId;

        for (var i = 0; i < wave; i++)
        {
            await bus.PublishAsync(new IdleWakeMessage(2));
        }

        var processedWhenPublishReturned = Volatile.Read(ref IdleWakeTracker.Processed);
        var inlineCount = Volatile.Read(ref IdleWakeTracker.InlineOnPublisher);

        var drained = true;
        try
        {
            await IdleWakeTracker.WaveDone(1 + wave).WaitAsync(TimeSpan.FromSeconds(10),
                TestContext.Current.CancellationToken);
        }
        catch (TimeoutException)
        {
            drained = false;
        }

        Console.WriteLine($"[GH-4167] runtime={Environment.Version}");
        Console.WriteLine($"[GH-4167] after publishing {wave}: processed={processedWhenPublishReturned}, inline-on-publisher={inlineCount}");
        Console.WriteLine($"[GH-4167] drained within 10s: {drained} (processed={Volatile.Read(ref IdleWakeTracker.Processed)}/{1 + wave})");

        drained.ShouldBeTrue(
            $"Only {Volatile.Read(ref IdleWakeTracker.Processed)} of {1 + wave} envelopes were ever processed.");
    }
}

public record IdleWakeMessage(int Wave);

public static class IdleWakeTracker
{
    public static int Processed;
    public static int CaptureWave2;
    public static int Wave2HandlerThreadId;
    public static int InlineOnPublisher;
    public static int PublisherThreadId;
    public static TaskCompletionSource FirstHandled = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private static TaskCompletionSource _waveDone = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private static int _waveTarget = int.MaxValue;

    public static Task WaveDone(int target)
    {
        Volatile.Write(ref _waveTarget, target);
        if (Volatile.Read(ref Processed) >= target)
        {
            _waveDone.TrySetResult();
        }

        return _waveDone.Task;
    }

    public static void Reset()
    {
        Processed = 0;
        CaptureWave2 = 0;
        Wave2HandlerThreadId = 0;
        InlineOnPublisher = 0;
        PublisherThreadId = 0;
        Volatile.Write(ref _waveTarget, int.MaxValue);
        _waveDone = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        FirstHandled = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
    }

    public static void Record()
    {
        if (Volatile.Read(ref CaptureWave2) == 1)
        {
            Interlocked.CompareExchange(ref Wave2HandlerThreadId, Environment.CurrentManagedThreadId, 0);

            if (Environment.CurrentManagedThreadId == Volatile.Read(ref PublisherThreadId))
            {
                Interlocked.Increment(ref InlineOnPublisher);
            }
        }

        var n = Interlocked.Increment(ref Processed);
        if (n >= Volatile.Read(ref _waveTarget))
        {
            _waveDone.TrySetResult();
        }
    }
}

public class IdleWakeHandler
{
    public void Handle(IdleWakeMessage message)
    {
        IdleWakeTracker.Record();

        if (message.Wave == 1)
        {
            IdleWakeTracker.FirstHandled.TrySetResult();
        }
    }
}
