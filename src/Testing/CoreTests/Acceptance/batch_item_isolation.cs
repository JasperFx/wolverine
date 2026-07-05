using JasperFx.Core;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Wolverine;
using Wolverine.Persistence.Durability;
using Xunit;

namespace CoreTests.Acceptance;

public class ApplyItemExceptionTests
{
    [Fact]
    public void deadletter_and_replay_others()
    {
        var a = new object();
        var ex = ApplyItemException.DeadLetterAndReplayOthers(a);
        ex.PoisonItems.ShouldHaveSingleItem().ShouldBeSameAs(a);
        ex.Disposition.ShouldBe(NonPoisonItems.Replay);
        ex.AckItems.ShouldBeEmpty();
    }

    [Fact]
    public void deadletter_and_replay_others_with_inner()
    {
        var a = new object();
        var inner = new InvalidOperationException("boom");
        var ex = ApplyItemException.DeadLetterAndReplayOthers(a, inner);
        ex.PoisonItems.ShouldHaveSingleItem().ShouldBeSameAs(a);
        ex.Disposition.ShouldBe(NonPoisonItems.Replay);
        ex.InnerException.ShouldBeSameAs(inner);
    }

    [Fact]
    public void deadletter_and_ack_others()
    {
        var a = new object();
        var b = new object();
        var ex = ApplyItemException.DeadLetterAndAckOthers(a, b);
        ex.PoisonItems.ShouldBe(new[] { a, b });
        ex.Disposition.ShouldBe(NonPoisonItems.AckAll);
    }

    [Fact]
    public void deadletter_with_selected_acks()
    {
        var poison = new object();
        var ack = new object();
        var ex = ApplyItemException.DeadLetter(new[] { poison }, new[] { ack });
        ex.PoisonItems.ShouldHaveSingleItem().ShouldBeSameAs(poison);
        ex.AckItems.ShouldHaveSingleItem().ShouldBeSameAs(ack);
        ex.Disposition.ShouldBe(NonPoisonItems.AckSelected);
    }

    [Fact]
    public void rejects_empty_or_null_poison()
    {
        Should.Throw<ArgumentException>(() => ApplyItemException.DeadLetterAndReplayOthers());
        Should.Throw<ArgumentException>(() => ApplyItemException.DeadLetterAndAckOthers(null!));
    }
}

public class batch_item_isolation : IAsyncLifetime
{
    private IHost _host = null!;
    private readonly CapturingDeadLetterInterceptor _deadLetters = new();

    public async Task InitializeAsync()
    {
        IsoItemBatchHandler.Reset();

        _host = await Host.CreateDefaultBuilder()
            .UseWolverine(opts =>
            {
                opts.Discovery.DisableConventionalDiscovery();
                opts.Discovery.IncludeType<IsoItemBatchHandler>();

                opts.Services.AddSingleton<IDeadLetterInterceptor>(_deadLetters);

                opts.BatchMessagesOf<IsoItem>(b =>
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

    private async Task publishAsync(params IsoItem[] items)
    {
        var bus = _host.MessageBus();
        foreach (var item in items)
        {
            await bus.PublishAsync(item);
        }
    }

    [Fact]
    public async Task deadletter_and_replay_others_isolates_the_poison_item()
    {
        IsoItemBatchHandler.Throw = items => ApplyItemException.DeadLetterAndReplayOthers(
            items.Single(x => x.Poison));

        await publishAsync(new IsoItem("a", false), new IsoItem("bad", true), new IsoItem("c", false));

        // The reduced batch (survivors only) is re-run to success.
        await IsoItemBatchHandler.SuccessSignal.Task.WaitAsync(10.Seconds());

        var successful = IsoItemBatchHandler.SuccessfulRuns.ShouldHaveSingleItem();
        successful.Select(x => x.Id).OrderBy(x => x).ShouldBe(new[] { "a", "c" });
        successful.ShouldNotContain(x => x.Poison);

        // Only the poison item was dead-lettered.
        _deadLetters.DeadLettered.OfType<IsoItem>().Select(x => x.Id).ShouldBe(new[] { "bad" });
    }

    [Fact]
    public async Task deadletter_and_ack_others_does_not_replay()
    {
        IsoItemBatchHandler.Throw = items => ApplyItemException.DeadLetterAndAckOthers(
            items.Single(x => x.Poison));

        await publishAsync(new IsoItem("a", false), new IsoItem("bad", true), new IsoItem("c", false));

        // Wait for the poison item to be dead-lettered, then confirm no replay happened.
        await _deadLetters.Signal.Task.WaitAsync(10.Seconds());
        await Task.Delay(500.Milliseconds());

        _deadLetters.DeadLettered.OfType<IsoItem>().Select(x => x.Id).ShouldBe(new[] { "bad" });

        // AckAll -> survivors acked as-is, handler NOT re-invoked, so no successful run recorded.
        IsoItemBatchHandler.SuccessfulRuns.ShouldBeEmpty();
        IsoItemBatchHandler.Invocations.ShouldBe(1);
    }

    [Fact]
    public async Task deadletter_with_selected_acks_replays_the_remainder()
    {
        IsoItemBatchHandler.Throw = items =>
        {
            var poison = items.Single(x => x.Poison);
            var ack = items.Single(x => x.Id == "ackme");
            return ApplyItemException.DeadLetter(new object[] { poison }, new object[] { ack });
        };

        await publishAsync(
            new IsoItem("ackme", false),
            new IsoItem("bad", true),
            new IsoItem("replay1", false),
            new IsoItem("replay2", false));

        await IsoItemBatchHandler.SuccessSignal.Task.WaitAsync(10.Seconds());

        // Only the two non-acked survivors are replayed; "ackme" was settled without a re-run.
        var successful = IsoItemBatchHandler.SuccessfulRuns.ShouldHaveSingleItem();
        successful.Select(x => x.Id).OrderBy(x => x).ShouldBe(new[] { "replay1", "replay2" });

        _deadLetters.DeadLettered.OfType<IsoItem>().Select(x => x.Id).ShouldBe(new[] { "bad" });
    }
}

public record IsoItem(string Id, bool Poison);

public class IsoItemBatchHandler
{
    private static readonly object _locker = new();

    public static Func<IsoItem[], ApplyItemException>? Throw;
    public static List<IsoItem[]> SuccessfulRuns { get; } = new();
    public static int Invocations;
    public static TaskCompletionSource SuccessSignal = new(TaskCreationOptions.RunContinuationsAsynchronously);

    public static void Reset()
    {
        Throw = null;
        lock (_locker)
        {
            SuccessfulRuns.Clear();
        }

        Invocations = 0;
        SuccessSignal = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
    }

    public void Handle(IsoItem[] items)
    {
        Interlocked.Increment(ref Invocations);

        // Only the first (poison-carrying) batch throws; the replayed survivor batch has no poison.
        if (Throw != null && items.Any(x => x.Poison))
        {
            throw Throw(items);
        }

        lock (_locker)
        {
            SuccessfulRuns.Add(items);
        }

        SuccessSignal.TrySetResult();
    }
}

public class CapturingDeadLetterInterceptor : IDeadLetterInterceptor
{
    public List<object> DeadLettered { get; } = new();
    public TaskCompletionSource Signal { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

    public ValueTask<Exception?> BeforeStoreAsync(Envelope envelope, Exception? exception,
        CancellationToken cancellation)
    {
        lock (DeadLettered)
        {
            if (envelope.Message != null)
            {
                DeadLettered.Add(envelope.Message);
            }
        }

        Signal.TrySetResult();
        return new ValueTask<Exception?>(exception);
    }
}
