using IntegrationTests;
using JasperFx.Resources;
using Marten;
using Microsoft.Extensions.Hosting;
using Shouldly;
using StronglyTypedIds;
using Wolverine;
using Wolverine.Marten;
using Wolverine.Persistence.Sagas;
using Wolverine.Tracking;

namespace MartenTests.Saga;

#region sample_strong_typed_id_saga

[StronglyTypedId(Template.Guid)]
public readonly partial struct OrderSagaId;

public class OrderSagaWorkflow : Wolverine.Saga
{
    public OrderSagaId Id { get; set; }

    public string CustomerName { get; set; }
    public bool ItemsPicked { get; set; }
    public bool PaymentProcessed { get; set; }
    public bool Shipped { get; set; }

    public static OrderSagaWorkflow Start(StartOrderSaga command)
    {
        return new OrderSagaWorkflow
        {
            Id = command.OrderId,
            CustomerName = command.CustomerName
        };
    }

    public void Handle(PickOrderItems command)
    {
        ItemsPicked = true;
        checkForCompletion();
    }

    public void Handle(ProcessOrderPayment command)
    {
        PaymentProcessed = true;
        checkForCompletion();
    }

    public void Handle(ShipOrder command)
    {
        Shipped = true;
        checkForCompletion();
    }

    public void Handle(CancelOrderSaga command)
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

// Messages using the strong-typed identifier
public record StartOrderSaga(OrderSagaId OrderId, string CustomerName);
public record PickOrderItems(OrderSagaId OrderSagaWorkflowId);
public record ProcessOrderPayment(OrderSagaId OrderSagaWorkflowId);
public record ShipOrder(OrderSagaId OrderSagaWorkflowId);
public record CancelOrderSaga(OrderSagaId OrderSagaWorkflowId);

// Message using [SagaIdentity] attribute with strong-typed ID
public class CompleteOrderStep
{
    [SagaIdentity] public OrderSagaId TheOrderId { get; set; }
}

#endregion

public class strong_typed_id_saga : PostgresqlContext, IAsyncLifetime
{
    private IHost _host;

    public async Task InitializeAsync()
    {
        _host = await Host.CreateDefaultBuilder()
            .UseWolverine(opts =>
            {
                opts.Services.AddMarten(m =>
                {
                    m.DisableNpgsqlLogging = true;
                    m.Connection(Servers.PostgresConnectionString);
                    m.DatabaseSchemaName = "strong_typed_sagas";
                }).IntegrateWithWolverine();

                opts.Services.AddResourceSetupOnStartup();
            }).StartAsync();
    }

    public async Task DisposeAsync()
    {
        await _host.StopAsync();
        _host.Dispose();
    }

    [Fact]
    public async Task start_saga_with_strong_typed_id()
    {
        var orderId = OrderSagaId.New();

        await _host.InvokeMessageAndWaitAsync(new StartOrderSaga(orderId, "Han Solo"));

        using var session = _host.DocumentStore().QuerySession();
        var saga = await session.LoadAsync<OrderSagaWorkflow>(orderId);

        saga.ShouldNotBeNull();
        saga.Id.ShouldBe(orderId);
        saga.CustomerName.ShouldBe("Han Solo");
    }

    [Fact]
    public async Task handle_message_with_strong_typed_id_on_existing_saga()
    {
        var orderId = OrderSagaId.New();

        await _host.InvokeMessageAndWaitAsync(new StartOrderSaga(orderId, "Luke Skywalker"));
        await _host.InvokeMessageAndWaitAsync(new PickOrderItems(orderId));

        using var session = _host.DocumentStore().QuerySession();
        var saga = await session.LoadAsync<OrderSagaWorkflow>(orderId);

        saga.ShouldNotBeNull();
        saga.ItemsPicked.ShouldBeTrue();
    }

    [Fact]
    public async Task complete_saga_with_strong_typed_id()
    {
        var orderId = OrderSagaId.New();

        await _host.InvokeMessageAndWaitAsync(new StartOrderSaga(orderId, "Leia Organa"));
        await _host.InvokeMessageAndWaitAsync(new PickOrderItems(orderId));
        await _host.InvokeMessageAndWaitAsync(new ProcessOrderPayment(orderId));
        await _host.InvokeMessageAndWaitAsync(new ShipOrder(orderId));

        using var session = _host.DocumentStore().QuerySession();
        var saga = await session.LoadAsync<OrderSagaWorkflow>(orderId);

        // Saga should be deleted when completed
        saga.ShouldBeNull();
    }

    [Fact]
    public async Task cancel_saga_with_strong_typed_id()
    {
        var orderId = OrderSagaId.New();

        await _host.InvokeMessageAndWaitAsync(new StartOrderSaga(orderId, "Chewbacca"));
        await _host.InvokeMessageAndWaitAsync(new CancelOrderSaga(orderId));

        using var session = _host.DocumentStore().QuerySession();
        var saga = await session.LoadAsync<OrderSagaWorkflow>(orderId);

        // Saga should be deleted after cancel (MarkCompleted)
        saga.ShouldBeNull();
    }

    [Fact]
    public async Task multiple_steps_with_strong_typed_id()
    {
        var orderId = OrderSagaId.New();

        await _host.InvokeMessageAndWaitAsync(new StartOrderSaga(orderId, "Yoda"));
        await _host.InvokeMessageAndWaitAsync(new PickOrderItems(orderId));
        await _host.InvokeMessageAndWaitAsync(new ProcessOrderPayment(orderId));

        using var session = _host.DocumentStore().QuerySession();
        var saga = await session.LoadAsync<OrderSagaWorkflow>(orderId);

        saga.ShouldNotBeNull();
        saga.ItemsPicked.ShouldBeTrue();
        saga.PaymentProcessed.ShouldBeTrue();
        saga.Shipped.ShouldBeFalse();
    }
}
