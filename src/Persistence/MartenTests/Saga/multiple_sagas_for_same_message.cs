using IntegrationTests;
using Marten;
using Microsoft.Extensions.Hosting;
using Shouldly;
using Wolverine;
using Wolverine.Attributes;
using Wolverine.Marten;
using Wolverine.Tracking;

namespace MartenTests.Saga;

public class multiple_sagas_for_same_message : IAsyncLifetime
{
    private IHost _host;

    public async Task InitializeAsync()
    {
        _host = await Host.CreateDefaultBuilder()
            .UseWolverine(opts =>
            {
                opts.MultipleHandlerBehavior = MultipleHandlerBehavior.Separated;

                opts.Discovery.IncludeType<ShippingSaga>();
                opts.Discovery.IncludeType<BillingSaga>();

                opts.Services.AddMarten(m =>
                {
                    m.DisableNpgsqlLogging = true;
                    m.Connection(Servers.PostgresConnectionString);
                    m.DatabaseSchemaName = "multi_saga";
                }).IntegrateWithWolverine();

                opts.Policies.AutoApplyTransactions();
            }).StartAsync();
    }

    public async Task DisposeAsync()
    {
        await _host.StopAsync();
    }

    [Fact]
    public async Task two_sagas_start_from_same_message()
    {
        var id = Guid.NewGuid();

        await _host.SendMessageAndWaitAsync(new OrderPlaced(id, "Widget"));

        await using var session = _host.DocumentStore().QuerySession();

        var shipping = await session.LoadAsync<ShippingSaga>(id);
        shipping.ShouldNotBeNull();
        shipping.ProductName.ShouldBe("Widget");

        var billing = await session.LoadAsync<BillingSaga>(id);
        billing.ShouldNotBeNull();
        billing.ProductName.ShouldBe("Widget");
    }

    [Fact]
    public async Task two_sagas_handle_subsequent_messages_independently()
    {
        var id = Guid.NewGuid();

        await _host.SendMessageAndWaitAsync(new OrderPlaced(id, "Gadget"));

        // Complete only the shipping saga
        await _host.SendMessageAndWaitAsync(new OrderShipped(id));

        await using var session = _host.DocumentStore().QuerySession();

        // Shipping saga should be deleted (completed)
        var shipping = await session.LoadAsync<ShippingSaga>(id);
        shipping.ShouldBeNull();

        // Billing saga should still exist
        var billing = await session.LoadAsync<BillingSaga>(id);
        billing.ShouldNotBeNull();
        billing.ProductName.ShouldBe("Gadget");

        // Now complete the billing saga
        await _host.SendMessageAndWaitAsync(new PaymentReceived(id));

        await using var session2 = _host.DocumentStore().QuerySession();
        (await session2.LoadAsync<BillingSaga>(id)).ShouldBeNull();
    }
}

// Shared message that both sagas react to
public record OrderPlaced(Guid OrderPlacedId, string ProductName);

// Messages specific to each saga
public record OrderShipped(Guid ShippingSagaId);
public record PaymentReceived(Guid BillingSagaId);

[WolverineIgnore]
public class ShippingSaga : Wolverine.Saga
{
    public Guid Id { get; set; }
    public string ProductName { get; set; } = string.Empty;

    public static ShippingSaga Start(OrderPlaced message)
    {
        return new ShippingSaga
        {
            Id = message.OrderPlacedId,
            ProductName = message.ProductName
        };
    }

    public void Handle(OrderShipped message)
    {
        MarkCompleted();
    }
}

[WolverineIgnore]
public class BillingSaga : Wolverine.Saga
{
    public Guid Id { get; set; }
    public string ProductName { get; set; } = string.Empty;

    public static BillingSaga Start(OrderPlaced message)
    {
        return new BillingSaga
        {
            Id = message.OrderPlacedId,
            ProductName = message.ProductName
        };
    }

    public void Handle(PaymentReceived message)
    {
        MarkCompleted();
    }
}
