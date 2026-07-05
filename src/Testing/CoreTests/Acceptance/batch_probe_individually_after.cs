using JasperFx.Core;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Wolverine;
using Wolverine.Persistence.Durability;
using Xunit;

namespace CoreTests.Acceptance;

public class batch_probe_individually_after : IAsyncLifetime
{
    private IHost _host = null!;
    private readonly CapturingDeadLetterInterceptor _deadLetters = new();

    public async Task InitializeAsync()
    {
        ProbeAfterHandler.Reset();

        _host = await Host.CreateDefaultBuilder()
            .UseWolverine(opts =>
            {
                opts.Discovery.DisableConventionalDiscovery();
                opts.Discovery.IncludeType<ProbeAfterHandler>();
                opts.Services.AddSingleton<IDeadLetterInterceptor>(_deadLetters);

                opts.BatchMessagesOf<ProbeAfterItem>(b =>
                {
                    b.BatchSize = 500;
                    b.TriggerTime = 1.Seconds();

                    // Retry the whole batch 3 times, then probe each member individually.
                    b.ProbeIndividuallyAfter(3);
                }).Sequential();
            }).StartAsync();
    }

    public async Task DisposeAsync()
    {
        await _host.StopAsync();
        _host.Dispose();
    }

    [Fact]
    public async Task retries_the_whole_batch_then_probes_individually()
    {
        var bus = _host.MessageBus();
        await bus.PublishAsync(new ProbeAfterItem("good1", false));
        await bus.PublishAsync(new ProbeAfterItem("bad", true));
        await bus.PublishAsync(new ProbeAfterItem("good2", false));

        await ProbeAfterHandler.BothGoodsSucceeded.Task.WaitAsync(15.Seconds());
        await _deadLetters.Signal.Task.WaitAsync(15.Seconds());

        // The whole 3-item batch was retried exactly 3 times before the probe kicked in.
        ProbeAfterHandler.WholeBatchAttempts.ShouldBe(3);

        // Then the probe isolated the poison item: the healthy members succeeded as singletons...
        ProbeAfterHandler.SucceededIds.OrderBy(x => x).ShouldBe(new[] { "good1", "good2" });

        // ...and only the poison item was dead-lettered.
        _deadLetters.DeadLettered.OfType<ProbeAfterItem>().Select(x => x.Id).ShouldBe(new[] { "bad" });
    }
}

public record ProbeAfterItem(string Id, bool Poison);

public class ProbeAfterFailure : Exception;

public class ProbeAfterHandler
{
    private static readonly object _locker = new();

    public static int WholeBatchAttempts;
    public static List<string> SucceededIds { get; } = new();
    public static TaskCompletionSource BothGoodsSucceeded = new(TaskCreationOptions.RunContinuationsAsynchronously);

    public static void Reset()
    {
        WholeBatchAttempts = 0;
        lock (_locker)
        {
            SucceededIds.Clear();
        }

        BothGoodsSucceeded = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
    }

    public void Handle(ProbeAfterItem[] items)
    {
        if (items.Any(x => x.Poison))
        {
            // Count only the whole-batch attempts (the poison singleton also lands here on the probe).
            if (items.Length > 1)
            {
                Interlocked.Increment(ref WholeBatchAttempts);
            }

            throw new ProbeAfterFailure();
        }

        lock (_locker)
        {
            foreach (var item in items)
            {
                SucceededIds.Add(item.Id);
            }

            if (SucceededIds.Count >= 2)
            {
                BothGoodsSucceeded.TrySetResult();
            }
        }
    }
}
