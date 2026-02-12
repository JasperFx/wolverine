using System.Collections.Concurrent;
using JasperFx.Core;
using JasperFx.Resources;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Shouldly;
using Wolverine.ErrorHandling;
using Wolverine.Runtime.Handlers;
using Xunit;
using Xunit.Abstractions;

namespace Wolverine.RabbitMQ.Tests.Bugs;

public class Bug_2078_pause_then_requeue : IAsyncLifetime
{
    private readonly ITestOutputHelper _output;
    private IHost _host;
    private readonly string _queueName;

    public Bug_2078_pause_then_requeue(ITestOutputHelper output)
    {
        _output = output;
        _queueName = RabbitTesting.NextQueueName();
    }

    public async Task InitializeAsync()
    {
        PauseThenRequeueHandler.Reset();

        _host = await Host.CreateDefaultBuilder()
            .UseWolverine(opts =>
            {
                opts.UseRabbitMq().AutoProvision().AutoPurgeOnStartup().DisableDeadLetterQueueing();

                opts.PublishMessage<PauseThenRequeueMessage>()
                    .ToRabbitQueue(_queueName);

                opts.ListenToRabbitQueue(_queueName)
                    .PreFetchCount(1);

                opts.LocalRoutingConventionDisabled = true;
            }).StartAsync();
    }

    public async Task DisposeAsync()
    {
        if (_host != null)
        {
            _host.TeardownResources();
            await _host.StopAsync();
            _host.Dispose();
        }
    }

    [Fact]
    public async Task pause_then_requeue_should_eventually_reprocess_message()
    {
        // Reproduce GH-2078: with PreFetchCount(1) and PauseThenRequeue,
        // the message should be reprocessed after the pause period.
        // The handler succeeds on attempt 2.
        var bus = _host.Services.GetRequiredService<IMessageBus>();
        await bus.PublishAsync(new PauseThenRequeueMessage());

        // With PauseThenRequeue(3.Seconds()), the message should:
        // 1. Fail on attempt 1
        // 2. Be requeued and listener paused for 3 seconds
        // 3. Succeed on attempt 2 after the pause
        // Total time: ~3-5 seconds
        // Without the fix, the message gets stuck with PreFetchCount(1) and never reprocesses.
        var success = await Poll(30.Seconds(), () => PauseThenRequeueHandler.Succeeded);

        var attempts = PauseThenRequeueHandler.Attempts.OrderBy(x => x).ToArray();

        _output.WriteLine($"Total handler invocations: {attempts.Length}");
        for (var i = 0; i < attempts.Length; i++)
        {
            _output.WriteLine($"  Attempt {i + 1}: {attempts[i]:O}");
        }

        if (attempts.Length >= 2)
        {
            for (var i = 1; i < attempts.Length; i++)
            {
                var gap = attempts[i] - attempts[i - 1];
                _output.WriteLine($"  Gap between attempt {i} and {i + 1}: {gap.TotalSeconds:F1}s");
            }
        }

        success.ShouldBeTrue("Message was never successfully reprocessed after PauseThenRequeue. " +
            "With PreFetchCount(1), the original un-ACKed message blocks the listener.");

        attempts.Length.ShouldBeGreaterThanOrEqualTo(2,
            "Expected at least 2 handler invocations (fail then succeed)");

        // Verify there is a meaningful gap (~3s) between attempts from the pause
        var firstGap = attempts[1] - attempts[0];
        _output.WriteLine($"\n  First gap: {firstGap.TotalSeconds:F1}s (expected ~3s)");
        firstGap.TotalSeconds.ShouldBeGreaterThan(2.0,
            $"Expected at least 2 seconds between attempts (configured 3s pause), but gap was {firstGap.TotalSeconds:F1}s");
    }

    private static async Task<bool> Poll(TimeSpan timeout, Func<bool> condition)
    {
        var cts = new CancellationTokenSource(timeout);
        while (!cts.IsCancellationRequested)
        {
            if (condition()) return true;
            await Task.Delay(250.Milliseconds());
        }

        return condition();
    }
}

public record PauseThenRequeueMessage;

public class PauseThenRequeueException : Exception
{
    public PauseThenRequeueException() : base("Rate limit exceeded") { }
}

public class PauseThenRequeueHandler
{
    public static readonly ConcurrentBag<DateTimeOffset> Attempts = new();
    public static volatile bool Succeeded;
    private static int _attemptCount;

    public static void Reset()
    {
        Attempts.Clear();
        Succeeded = false;
        _attemptCount = 0;
    }

    public static void Configure(HandlerChain chain)
    {
        chain.OnException<PauseThenRequeueException>()
            .PauseThenRequeue(3.Seconds());
    }

    public static void Handle(PauseThenRequeueMessage message)
    {
        Attempts.Add(DateTimeOffset.UtcNow);
        var attempt = Interlocked.Increment(ref _attemptCount);

        if (attempt == 1)
        {
            throw new PauseThenRequeueException();
        }

        // Succeed on attempt 2+
        Succeeded = true;
    }
}
