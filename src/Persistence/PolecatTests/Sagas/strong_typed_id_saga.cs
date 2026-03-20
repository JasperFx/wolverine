using IntegrationTests;
using JasperFx.Resources;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Polecat;
using Shouldly;
using StronglyTypedIds;
using Wolverine;
using Wolverine.Polecat;
using Wolverine.Persistence.Sagas;
using Wolverine.Tracking;

namespace PolecatTests.Sagas;

[StronglyTypedId(Template.Guid)]
public readonly partial struct PcOrderSagaId;

public class PcOrderSagaWorkflow : Wolverine.Saga
{
    public PcOrderSagaId Id { get; set; }

    public string CustomerName { get; set; }
    public bool ItemsPicked { get; set; }
    public bool PaymentProcessed { get; set; }
    public bool Shipped { get; set; }

    public static PcOrderSagaWorkflow Start(StartPcOrderSaga command)
    {
        return new PcOrderSagaWorkflow
        {
            Id = command.OrderId,
            CustomerName = command.CustomerName
        };
    }

    public void Handle(PickPcOrderItems command)
    {
        ItemsPicked = true;
        checkForCompletion();
    }

    public void Handle(ProcessPcOrderPayment command)
    {
        PaymentProcessed = true;
        checkForCompletion();
    }

    public void Handle(ShipPcOrder command)
    {
        Shipped = true;
        checkForCompletion();
    }

    public void Handle(CancelPcOrderSaga command)
    {
        MarkCompleted();
    }

    private void checkForCompletion()
    {
        if (ItemsPicked && PaymentProcessed && Shipped)
        {
            MarkCompleted();
        }
    }
}

public record StartPcOrderSaga(PcOrderSagaId OrderId, string CustomerName);
public record PickPcOrderItems(PcOrderSagaId PcOrderSagaWorkflowId);
public record ProcessPcOrderPayment(PcOrderSagaId PcOrderSagaWorkflowId);
public record ShipPcOrder(PcOrderSagaId PcOrderSagaWorkflowId);
public record CancelPcOrderSaga(PcOrderSagaId PcOrderSagaWorkflowId);

public class CompletePcOrderStep
{
    [SagaIdentity] public PcOrderSagaId TheOrderId { get; set; }
}

public class strong_typed_id_saga : IAsyncLifetime
{
    private IHost _host;

    public async Task InitializeAsync()
    {
        _host = await Host.CreateDefaultBuilder()
            .UseWolverine(opts =>
            {
                opts.Services.AddPolecat(m =>
                {
                    m.ConnectionString = Servers.SqlServerConnectionString;
                    m.DatabaseSchemaName = "strong_typed_sagas";
                }).IntegrateWithWolverine();

                opts.Services.AddResourceSetupOnStartup();
            }).StartAsync();

        await ((DocumentStore)_host.Services.GetRequiredService<IDocumentStore>()).Database
            .ApplyAllConfiguredChangesToDatabaseAsync();
    }

    public async Task DisposeAsync()
    {
        await _host.StopAsync();
        _host.Dispose();
    }

    [Fact]
    public async Task start_saga_with_strong_typed_id()
    {
        var orderId = PcOrderSagaId.New();

        await _host.InvokeMessageAndWaitAsync(new StartPcOrderSaga(orderId, "Han Solo"));

        await using var session = _host.Services.GetRequiredService<IDocumentStore>().QuerySession();
        var saga = await session.LoadAsync<PcOrderSagaWorkflow>(orderId.Value);

        saga.ShouldNotBeNull();
        saga.Id.ShouldBe(orderId);
        saga.CustomerName.ShouldBe("Han Solo");
    }

    [Fact]
    public async Task handle_message_with_strong_typed_id_on_existing_saga()
    {
        var orderId = PcOrderSagaId.New();

        await _host.InvokeMessageAndWaitAsync(new StartPcOrderSaga(orderId, "Luke Skywalker"));
        await _host.InvokeMessageAndWaitAsync(new PickPcOrderItems(orderId));

        await using var session = _host.Services.GetRequiredService<IDocumentStore>().QuerySession();
        var saga = await session.LoadAsync<PcOrderSagaWorkflow>(orderId.Value);

        saga.ShouldNotBeNull();
        saga.ItemsPicked.ShouldBeTrue();
    }

    [Fact]
    public async Task complete_saga_with_strong_typed_id()
    {
        var orderId = PcOrderSagaId.New();

        await _host.InvokeMessageAndWaitAsync(new StartPcOrderSaga(orderId, "Leia Organa"));
        await _host.InvokeMessageAndWaitAsync(new PickPcOrderItems(orderId));
        await _host.InvokeMessageAndWaitAsync(new ProcessPcOrderPayment(orderId));
        await _host.InvokeMessageAndWaitAsync(new ShipPcOrder(orderId));

        await using var session = _host.Services.GetRequiredService<IDocumentStore>().QuerySession();
        var saga = await session.LoadAsync<PcOrderSagaWorkflow>(orderId.Value);

        // Saga should be deleted when completed
        saga.ShouldBeNull();
    }

    [Fact]
    public async Task cancel_saga_with_strong_typed_id()
    {
        var orderId = PcOrderSagaId.New();

        await _host.InvokeMessageAndWaitAsync(new StartPcOrderSaga(orderId, "Chewbacca"));
        await _host.InvokeMessageAndWaitAsync(new CancelPcOrderSaga(orderId));

        await using var session = _host.Services.GetRequiredService<IDocumentStore>().QuerySession();
        var saga = await session.LoadAsync<PcOrderSagaWorkflow>(orderId.Value);

        // Saga should be deleted after cancel (MarkCompleted)
        saga.ShouldBeNull();
    }

    [Fact]
    public async Task multiple_steps_with_strong_typed_id()
    {
        var orderId = PcOrderSagaId.New();

        await _host.InvokeMessageAndWaitAsync(new StartPcOrderSaga(orderId, "Yoda"));
        await _host.InvokeMessageAndWaitAsync(new PickPcOrderItems(orderId));
        await _host.InvokeMessageAndWaitAsync(new ProcessPcOrderPayment(orderId));

        await using var session = _host.Services.GetRequiredService<IDocumentStore>().QuerySession();
        var saga = await session.LoadAsync<PcOrderSagaWorkflow>(orderId.Value);

        saga.ShouldNotBeNull();
        saga.ItemsPicked.ShouldBeTrue();
        saga.PaymentProcessed.ShouldBeTrue();
        saga.Shipped.ShouldBeFalse();
    }
}
