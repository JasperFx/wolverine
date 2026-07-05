using JasperFx.Core;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Wolverine;
using Wolverine.ErrorHandling;
using Wolverine.Persistence.Durability;
using Xunit;

namespace CoreTests.Acceptance;

public class batch_isolate_members : IAsyncLifetime
{
    private IHost _host = null!;
    private readonly CapturingDeadLetterInterceptor _deadLetters = new();

    public async Task InitializeAsync()
    {
        ProbeItemBatchHandler.Reset();

        _host = await Host.CreateDefaultBuilder()
            .UseWolverine(opts =>
            {
                opts.Discovery.DisableConventionalDiscovery();
                opts.Discovery.IncludeType<ProbeItemBatchHandler>();

                opts.Services.AddSingleton<IDeadLetterInterceptor>(_deadLetters);

                // The flagship composition from the plan: a deterministic error type isolates the bad
                // member, while transient errors (not exercised here) would retry the whole batch.
                opts.Policies.OnException<ProbeFailure>().IsolateBatchMembers();

                opts.BatchMessagesOf<ProbeItem>(b =>
                {
                    b.BatchSize = 500;
                    b.TriggerTime = 1.Seconds();
                }).Sequential();
            }).StartAsync();
    }

    public async Task DisposeAsync()
    {
        await _host.StopAsync();
        _host.Dispose();
    }

    [Fact]
    public async Task isolates_the_failing_member_by_probing_individually()
    {
        var bus = _host.MessageBus();
        await bus.PublishAsync(new ProbeItem("good1", false));
        await bus.PublishAsync(new ProbeItem("bad", true));
        await bus.PublishAsync(new ProbeItem("good2", false));

        // The whole batch throws the opaque ProbeFailure; IsolateBatchMembers re-runs each member as its
        // own size-1 batch. The two healthy singletons succeed; the poison singleton dead-letters.
        await ProbeItemBatchHandler.BothGoodsSucceeded.Task.WaitAsync(10.Seconds());
        await _deadLetters.Signal.Task.WaitAsync(10.Seconds());

        ProbeItemBatchHandler.SucceededIds.OrderBy(x => x).ShouldBe(new[] { "good1", "good2" });

        // Only the poison item was dead-lettered - the healthy members were not collateral damage.
        _deadLetters.DeadLettered.OfType<ProbeItem>().Select(x => x.Id).ShouldBe(new[] { "bad" });
    }
}

public class isolate_batch_members_on_a_non_batched_message : IAsyncLifetime
{
    private IHost _host = null!;
    private readonly CapturingDeadLetterInterceptor _deadLetters = new();

    public async Task InitializeAsync()
    {
        _host = await Host.CreateDefaultBuilder()
            .UseWolverine(opts =>
            {
                opts.Discovery.DisableConventionalDiscovery();
                opts.Discovery.IncludeType<SoloProbeHandler>();
                opts.Services.AddSingleton<IDeadLetterInterceptor>(_deadLetters);

                // IsolateBatchMembers on a message type that is NOT batched must behave like a plain
                // move-to-error-queue: there is nothing to isolate.
                opts.Policies.OnException<ProbeFailure>().IsolateBatchMembers();
            }).StartAsync();
    }

    public async Task DisposeAsync()
    {
        await _host.StopAsync();
        _host.Dispose();
    }

    [Fact]
    public async Task falls_back_to_dead_lettering_the_single_message()
    {
        await _host.MessageBus().PublishAsync(new SoloProbe("only"));

        await _deadLetters.Signal.Task.WaitAsync(10.Seconds());

        _deadLetters.DeadLettered.OfType<SoloProbe>().Select(x => x.Id).ShouldBe(new[] { "only" });
    }
}

public record SoloProbe(string Id);

public class SoloProbeHandler
{
    public void Handle(SoloProbe message)
    {
        throw new ProbeFailure();
    }
}

public record ProbeItem(string Id, bool Poison);

// Opaque failure: the handler cannot name which item is bad, it just throws.
public class ProbeFailure : Exception;

public class ProbeItemBatchHandler
{
    private static readonly object _locker = new();

    public static List<string> SucceededIds { get; } = new();
    public static TaskCompletionSource BothGoodsSucceeded = new(TaskCreationOptions.RunContinuationsAsynchronously);

    public static void Reset()
    {
        lock (_locker)
        {
            SucceededIds.Clear();
        }

        BothGoodsSucceeded = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
    }

    public void Handle(ProbeItem[] items)
    {
        if (items.Any(x => x.Poison))
        {
            throw new ProbeFailure();
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
