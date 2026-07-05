using JasperFx.Core;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Wolverine;
using Wolverine.Persistence.Durability;
using Xunit;

namespace CoreTests.Acceptance;

public class batch_coalesce_poison : IAsyncLifetime
{
    private IHost _host = null!;
    private readonly CapturingDeadLetterInterceptor _deadLetters = new();

    public async Task InitializeAsync()
    {
        CoalPoisonHandler.Reset();

        _host = await Host.CreateDefaultBuilder()
            .UseWolverine(opts =>
            {
                opts.Discovery.DisableConventionalDiscovery();
                opts.Discovery.IncludeType<CoalPoisonHandler>();
                opts.Services.AddSingleton<IDeadLetterInterceptor>(_deadLetters);

                opts.BatchMessagesOf<CoalItem>(b =>
                {
                    b.BatchSize = 500;
                    b.TriggerTime = 1.Seconds();
                    b.CoalesceBy((CoalItem x) => x.Key);
                }).Sequential();
            }).StartAsync();
    }

    public async Task DisposeAsync()
    {
        await _host.StopAsync();
        _host.Dispose();
    }

    [Fact]
    public async Task poisoning_a_coalesced_item_dead_letters_every_member_of_that_key()
    {
        var bus = _host.MessageBus();
        // Key "A" has three members; the last-wins (v3) is the poison the handler sees. Key "B" is healthy.
        await bus.PublishAsync(new CoalItem("A", 1, false));
        await bus.PublishAsync(new CoalItem("A", 2, false));
        await bus.PublishAsync(new CoalItem("A", 3, true));
        await bus.PublishAsync(new CoalItem("B", 1, false));

        await CoalPoisonHandler.SurvivorSucceeded.Task.WaitAsync(10.Seconds());
        await _deadLetters.Signal.Task.WaitAsync(10.Seconds());

        // Every member that collapsed into the poisoned key "A" is dead-lettered - all three versions.
        var deadLettered = _deadLetters.DeadLettered.OfType<CoalItem>().ToArray();
        deadLettered.Where(x => x.Key == "A").Select(x => x.Version).OrderBy(x => x)
            .ShouldBe(new[] { 1, 2, 3 });
        deadLettered.ShouldNotContain(x => x.Key == "B");

        // The healthy key survived and was replayed to success.
        var survivor = CoalPoisonHandler.SuccessfulRuns.ShouldHaveSingleItem();
        survivor.Select(x => x.Key).ShouldBe(new[] { "B" });
    }
}

public record CoalItem(string Key, int Version, bool Poison);

public class CoalPoisonHandler
{
    private static readonly object _locker = new();

    public static List<CoalItem[]> SuccessfulRuns { get; } = new();
    public static TaskCompletionSource SurvivorSucceeded = new(TaskCreationOptions.RunContinuationsAsynchronously);

    public static void Reset()
    {
        lock (_locker)
        {
            SuccessfulRuns.Clear();
        }

        SurvivorSucceeded = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
    }

    public void Handle(CoalItem[] items)
    {
        var poison = items.FirstOrDefault(x => x.Poison);
        if (poison != null)
        {
            // Flag the single coalesced (last-wins) instance the handler sees; Wolverine expands it to
            // every member that collapsed into that key.
            throw ApplyItemException.DeadLetterAndReplayOthers(poison);
        }

        lock (_locker)
        {
            SuccessfulRuns.Add(items);
        }

        SurvivorSucceeded.TrySetResult();
    }
}
