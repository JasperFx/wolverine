using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Shouldly;
using Wolverine;
using Wolverine.Persistence.Sagas;
using Wolverine.Tracking;
using Xunit;

namespace SlowTests.Persistence.Sagas;

public record StartGuardedSaga(Guid Id);

public record GuardedCommand(Guid SagaId, int? Order) : SequencedMessage;

/// <summary>
/// GH-4175. Overrides the hook to DISCARD anything whose order has already been passed, and records
/// that it was asked -- which is the whole point: before this hook the arrival was invisible.
/// </summary>
public class DiscardingResequencerSaga : ResequencerSaga<GuardedCommand>
{
    public static readonly List<(int? Order, int LastSequence)> Rejected = new();

    public Guid Id { get; set; }
    public List<int?> ProcessedOrders { get; set; } = new();

    public static DiscardingResequencerSaga Start(StartGuardedSaga cmd) => new() { Id = cmd.Id };

    protected override bool ShouldHandleAlreadySequenced(GuardedCommand message, IMessageBus bus)
    {
        lock (Rejected)
        {
            Rejected.Add((message.Order, LastSequence));
        }

        return false;
    }

    public void Handle(GuardedCommand cmd)
    {
        ProcessedOrders.Add(cmd.Order);
    }
}

public class resequencer_saga_duplicate_detection : IAsyncLifetime
{
    private IHost _host = null!;

    public async ValueTask InitializeAsync()
    {
        lock (DiscardingResequencerSaga.Rejected)
        {
            DiscardingResequencerSaga.Rejected.Clear();
        }

        _host = await Host.CreateDefaultBuilder()
            .UseWolverine(opts =>
            {
                opts.Discovery.DisableConventionalDiscovery()
                    .IncludeType<DiscardingResequencerSaga>();

                opts.PublishAllMessages().ToLocalQueue("guarded");
                opts.LocalQueue("guarded").Sequential();
            })
            .StartAsync();
    }

    public async ValueTask DisposeAsync()
    {
        await _host.StopAsync();
        _host.Dispose();
    }

    private DiscardingResequencerSaga LoadState(Guid id)
    {
        return _host.Services.GetRequiredService<InMemorySagaPersistor>()
            .Load<DiscardingResequencerSaga>(id)!;
    }

    [Fact]
    public async Task an_already_passed_order_reaches_the_hook_and_can_be_discarded()
    {
        var sagaId = Guid.NewGuid();

        await _host.InvokeMessageAndWaitAsync(new StartGuardedSaga(sagaId));
        await _host.InvokeMessageAndWaitAsync(new GuardedCommand(sagaId, 1));
        await _host.InvokeMessageAndWaitAsync(new GuardedCommand(sagaId, 2));

        LoadState(sagaId).ProcessedOrders.ShouldBe([1, 2]);

        // 1 arrives again, after the saga has already passed it
        await _host.InvokeMessageAndWaitAsync(new GuardedCommand(sagaId, 1));

        var state = LoadState(sagaId);

        // The override discarded it, so it was NOT handled a second time
        state.ProcessedOrders.ShouldBe([1, 2]);
        state.LastSequence.ShouldBe(2);
        state.Pending.ShouldBeEmpty();

        // ...and, critically, the saga was told about it. Before GH-4175 this arrival was invisible.
        lock (DiscardingResequencerSaga.Rejected)
        {
            DiscardingResequencerSaga.Rejected.ShouldContain((1, 2));
        }
    }

    [Fact]
    public async Task the_hook_is_not_called_for_a_legitimate_replay_out_of_pending()
    {
        var sagaId = Guid.NewGuid();

        await _host.InvokeMessageAndWaitAsync(new StartGuardedSaga(sagaId));

        // 2 and 3 land in Pending, then 1 fills the gap and both are replayed out of Pending.
        // Since GH-4172 the drain hands back LastSequence + 1 without advancing the counter, so a
        // replay takes the normal path -- it must never look like a duplicate to this hook.
        await _host.InvokeMessageAndWaitAsync(new GuardedCommand(sagaId, 3));
        await _host.InvokeMessageAndWaitAsync(new GuardedCommand(sagaId, 2));

        await _host.ExecuteAndWaitAsync(async () =>
        {
            await _host.MessageBus().PublishAsync(new GuardedCommand(sagaId, 1));
        }, timeoutInMilliseconds: 30000);

        var state = LoadState(sagaId);
        state.ProcessedOrders.ShouldBe([1, 2, 3]);
        state.LastSequence.ShouldBe(3);

        // If replays were still reaching the hook, the override above would have discarded them and
        // ProcessedOrders would be short -- but assert on the hook directly too
        lock (DiscardingResequencerSaga.Rejected)
        {
            DiscardingResequencerSaga.Rejected.ShouldBeEmpty();
        }
    }

    [Fact]
    public async Task a_message_with_no_order_still_bypasses_the_hook()
    {
        var sagaId = Guid.NewGuid();

        await _host.InvokeMessageAndWaitAsync(new StartGuardedSaga(sagaId));
        await _host.InvokeMessageAndWaitAsync(new GuardedCommand(sagaId, 1));
        await _host.InvokeMessageAndWaitAsync(new GuardedCommand(sagaId, null));
        await _host.InvokeMessageAndWaitAsync(new GuardedCommand(sagaId, 0));

        LoadState(sagaId).ProcessedOrders.ShouldBe([1, null, 0]);

        lock (DiscardingResequencerSaga.Rejected)
        {
            DiscardingResequencerSaga.Rejected.ShouldBeEmpty();
        }
    }
}
