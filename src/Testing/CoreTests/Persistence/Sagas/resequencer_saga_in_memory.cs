using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Shouldly;
using Wolverine;
using Wolverine.Persistence.Sagas;
using Wolverine.Tracking;
using Wolverine.Transports;
using Xunit;

namespace CoreTests.Persistence.Sagas;

public record StartSequencedSaga(Guid Id);

public record SequencedCommand(Guid SagaId, int? Order) : SequencedMessage;

public class TestResequencerSaga : ResequencerSaga<SequencedCommand>
{
    public Guid Id { get; set; }
    public List<int?> ProcessedOrders { get; set; } = new();

    public static TestResequencerSaga Start(StartSequencedSaga cmd)
    {
        return new TestResequencerSaga { Id = cmd.Id };
    }

    public void Handle(SequencedCommand cmd)
    {
        ProcessedOrders.Add(cmd.Order);
    }
}

public class resequencer_saga_in_memory : IAsyncLifetime
{
    private IHost _host;

    public async Task InitializeAsync()
    {
        _host = await Host.CreateDefaultBuilder()
            .UseWolverine(opts =>
            {
                opts.Discovery.DisableConventionalDiscovery()
                    .IncludeType<TestResequencerSaga>();

                opts.PublishAllMessages().To(TransportConstants.LocalUri);
            })
            .StartAsync();
    }

    public async Task DisposeAsync()
    {
        await _host.StopAsync();
        _host.Dispose();
    }

    private async Task<TestResequencerSaga?> LoadState(Guid id)
    {
        return _host.Services.GetRequiredService<InMemorySagaPersistor>()
            .Load<TestResequencerSaga>(id);
    }

    [Fact]
    public async Task messages_in_order_are_all_handled()
    {
        var sagaId = Guid.NewGuid();

        await _host.InvokeMessageAndWaitAsync(new StartSequencedSaga(sagaId));
        await _host.InvokeMessageAndWaitAsync(new SequencedCommand(sagaId, 1));
        await _host.InvokeMessageAndWaitAsync(new SequencedCommand(sagaId, 2));
        await _host.InvokeMessageAndWaitAsync(new SequencedCommand(sagaId, 3));

        var state = await LoadState(sagaId);
        state.ShouldNotBeNull();
        state.ProcessedOrders.ShouldBe([1, 2, 3]);
        state.LastSequence.ShouldBe(3);
        state.Pending.ShouldBeEmpty();
    }

    [Fact]
    public async Task out_of_order_message_is_queued_not_handled()
    {
        var sagaId = Guid.NewGuid();

        await _host.InvokeMessageAndWaitAsync(new StartSequencedSaga(sagaId));
        await _host.InvokeMessageAndWaitAsync(new SequencedCommand(sagaId, 3));

        var state = await LoadState(sagaId);
        state.ShouldNotBeNull();
        state.ProcessedOrders.ShouldBeEmpty();
        state.LastSequence.ShouldBe(0);
        state.Pending.Count.ShouldBe(1);
    }

    [Fact]
    public async Task out_of_order_messages_replayed_when_gap_fills()
    {
        var sagaId = Guid.NewGuid();

        await _host.InvokeMessageAndWaitAsync(new StartSequencedSaga(sagaId));
        await _host.InvokeMessageAndWaitAsync(new SequencedCommand(sagaId, 3));
        await _host.InvokeMessageAndWaitAsync(new SequencedCommand(sagaId, 2));

        // Send message 1 which fills the gap - ShouldProceed will republish 2 and 3
        // The tracked session will wait for all cascading messages to complete
        await _host.ExecuteAndWaitAsync(async () =>
        {
            await _host.Services.GetRequiredService<IMessageBus>()
                .PublishAsync(new SequencedCommand(sagaId, 1));
        }, timeoutInMilliseconds: 30000);

        var state = await LoadState(sagaId);
        state.ShouldNotBeNull();
        state.LastSequence.ShouldBe(3);
        state.Pending.ShouldBeEmpty();
        state.ProcessedOrders.ShouldContain(1);
        state.ProcessedOrders.ShouldContain(2);
        state.ProcessedOrders.ShouldContain(3);
    }

    [Fact]
    public async Task null_order_bypasses_guard()
    {
        var sagaId = Guid.NewGuid();

        await _host.InvokeMessageAndWaitAsync(new StartSequencedSaga(sagaId));
        await _host.InvokeMessageAndWaitAsync(new SequencedCommand(sagaId, null));

        var state = await LoadState(sagaId);
        state.ShouldNotBeNull();
        state.ProcessedOrders.ShouldBe([null]);
    }

    [Fact]
    public async Task zero_order_bypasses_guard()
    {
        var sagaId = Guid.NewGuid();

        await _host.InvokeMessageAndWaitAsync(new StartSequencedSaga(sagaId));
        await _host.InvokeMessageAndWaitAsync(new SequencedCommand(sagaId, 0));

        var state = await LoadState(sagaId);
        state.ShouldNotBeNull();
        state.ProcessedOrders.ShouldBe([0]);
    }
}
