using IntegrationTests;
using JasperFx.Resources;
using Marten;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Shouldly;
using Wolverine;
using Wolverine.Marten;
using Wolverine.Tracking;
using Xunit;

namespace MartenTests.Saga;

public record StartMartenSequencedSaga(Guid Id);

public record MartenSequencedCommand(Guid SagaId, int? Order) : SequencedMessage;

public class MartenTestResequencerSaga : ResequencerSaga<MartenSequencedCommand>
{
    public Guid Id { get; set; }
    public List<int?> ProcessedOrders { get; set; } = new();

    public static MartenTestResequencerSaga Start(StartMartenSequencedSaga cmd)
    {
        return new MartenTestResequencerSaga { Id = cmd.Id };
    }

    public void Handle(MartenSequencedCommand cmd)
    {
        ProcessedOrders.Add(cmd.Order);
    }
}

public class resequencer_saga_end_to_end : PostgresqlContext, IAsyncLifetime
{
    private IHost _host = null!;

    public async Task InitializeAsync()
    {
        _host = await Host.CreateDefaultBuilder()
            .UseWolverine(opts =>
            {
                opts.Services.AddMarten(m =>
                {
                    m.DisableNpgsqlLogging = true;
                    m.Connection(Servers.PostgresConnectionString);
                    m.DatabaseSchemaName = "resequencer_sagas";
                }).IntegrateWithWolverine();

                opts.Services.AddResourceSetupOnStartup();

                opts.Discovery.DisableConventionalDiscovery()
                    .IncludeType<MartenTestResequencerSaga>();
            })
            .StartAsync();
    }

    public async Task DisposeAsync()
    {
        await _host.StopAsync();
        _host.Dispose();
    }

    private async Task<MartenTestResequencerSaga?> LoadState(Guid id)
    {
        using var session = _host.DocumentStore().QuerySession();
        return await session.LoadAsync<MartenTestResequencerSaga>(id);
    }

    [Fact]
    public async Task messages_in_order_are_all_handled()
    {
        var sagaId = Guid.NewGuid();

        await _host.InvokeMessageAndWaitAsync(new StartMartenSequencedSaga(sagaId));
        await _host.InvokeMessageAndWaitAsync(new MartenSequencedCommand(sagaId, 1));
        await _host.InvokeMessageAndWaitAsync(new MartenSequencedCommand(sagaId, 2));
        await _host.InvokeMessageAndWaitAsync(new MartenSequencedCommand(sagaId, 3));

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

        await _host.InvokeMessageAndWaitAsync(new StartMartenSequencedSaga(sagaId));
        await _host.InvokeMessageAndWaitAsync(new MartenSequencedCommand(sagaId, 3));

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

        await _host.InvokeMessageAndWaitAsync(new StartMartenSequencedSaga(sagaId));
        // Send message 2 first, it will be queued in Pending
        await _host.InvokeMessageAndWaitAsync(new MartenSequencedCommand(sagaId, 2));

        // Send message 1 which fills the gap - ShouldProceed will republish 2
        await _host.ExecuteAndWaitAsync(async () =>
        {
            await _host.Services.GetRequiredService<IMessageBus>()
                .PublishAsync(new MartenSequencedCommand(sagaId, 1));
        }, timeoutInMilliseconds: 30000);

        var state = await LoadState(sagaId);
        state.ShouldNotBeNull();
        state.LastSequence.ShouldBe(2);
        state.Pending.ShouldBeEmpty();
        state.ProcessedOrders.ShouldContain(1);
        state.ProcessedOrders.ShouldContain(2);
    }

    [Fact]
    public async Task null_order_bypasses_guard()
    {
        var sagaId = Guid.NewGuid();

        await _host.InvokeMessageAndWaitAsync(new StartMartenSequencedSaga(sagaId));
        await _host.InvokeMessageAndWaitAsync(new MartenSequencedCommand(sagaId, null));

        var state = await LoadState(sagaId);
        state.ShouldNotBeNull();
        state.ProcessedOrders.ShouldBe([null]);
    }
}
