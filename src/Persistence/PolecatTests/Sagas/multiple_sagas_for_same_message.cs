using IntegrationTests;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Polecat;
using Shouldly;
using Wolverine;
using Wolverine.Attributes;
using Wolverine.Polecat;
using Wolverine.Tracking;

namespace PolecatTests.Sagas;

public class multiple_sagas_for_same_message : IAsyncLifetime
{
    private IHost _host;

    public async Task InitializeAsync()
    {
        _host = await Host.CreateDefaultBuilder()
            .UseWolverine(opts =>
            {
                opts.MultipleHandlerBehavior = MultipleHandlerBehavior.Separated;

                opts.Discovery.IncludeType<PcShippingSaga>();
                opts.Discovery.IncludeType<PcBillingSaga>();

                opts.Services.AddPolecat(m =>
                {
                    m.ConnectionString = Servers.SqlServerConnectionString;
                    m.DatabaseSchemaName = "multi_saga";
                }).IntegrateWithWolverine();

                opts.Policies.AutoApplyTransactions();
            }).StartAsync();

        await ((DocumentStore)_host.Services.GetRequiredService<IDocumentStore>()).Database
            .ApplyAllConfiguredChangesToDatabaseAsync();
    }

    public async Task DisposeAsync()
    {
        await _host.StopAsync();
    }

    [Fact]
    public async Task two_sagas_start_from_same_message()
    {
        var id = Guid.NewGuid();

        await _host.SendMessageAndWaitAsync(new PcOrderPlaced(id, "Widget"));

        await using var session = _host.Services.GetRequiredService<IDocumentStore>().QuerySession();

        var shipping = await session.LoadAsync<PcShippingSaga>(id);
        shipping.ShouldNotBeNull();
        shipping.ProductName.ShouldBe("Widget");

        var billing = await session.LoadAsync<PcBillingSaga>(id);
        billing.ShouldNotBeNull();
        billing.ProductName.ShouldBe("Widget");
    }

    [Fact]
    public async Task two_sagas_handle_subsequent_messages_independently()
    {
        var id = Guid.NewGuid();

        await using var session = _host.Services.GetRequiredService<IDocumentStore>().QuerySession();

        await _host.SendMessageAndWaitAsync(new PcOrderPlaced(id, "Gadget"));
        (await session.LoadAsync<PcShippingSaga>(id)).ShouldNotBeNull();
        (await session.LoadAsync<PcBillingSaga>(id)).ShouldNotBeNull();

        // Complete only the shipping saga
        await _host.SendMessageAndWaitAsync(new PcOrderShipped(id));

        // Shipping saga should be deleted (completed)
        var shipping = await session.LoadAsync<PcShippingSaga>(id);
        shipping.ShouldBeNull();

        // Billing saga should still exist
        var billing = await session.LoadAsync<PcBillingSaga>(id);
        billing.ShouldNotBeNull();
        billing.ProductName.ShouldBe("Gadget");

        // Now complete the billing saga
        await _host.SendMessageAndWaitAsync(new PcPaymentReceived(id));

        await using var session2 = _host.Services.GetRequiredService<IDocumentStore>().QuerySession();
        (await session2.LoadAsync<PcBillingSaga>(id)).ShouldBeNull();
    }
}

// Shared message that both sagas react to
public record PcOrderPlaced(Guid PcOrderPlacedId, string ProductName);

// Messages specific to each saga
public record PcOrderShipped(Guid PcShippingSagaId);
public record PcPaymentReceived(Guid PcBillingSagaId);

[WolverineIgnore]
public class PcShippingSaga : Wolverine.Saga
{
    public Guid Id { get; set; }
    public string ProductName { get; set; } = string.Empty;

    public static PcShippingSaga Start(PcOrderPlaced message)
    {
        return new PcShippingSaga
        {
            Id = message.PcOrderPlacedId,
            ProductName = message.ProductName
        };
    }

    public void Handle(PcOrderShipped message)
    {
        MarkCompleted();
    }
}

[WolverineIgnore]
public class PcBillingSaga : Wolverine.Saga
{
    public Guid Id { get; set; }
    public string ProductName { get; set; } = string.Empty;

    public static PcBillingSaga Start(PcOrderPlaced message)
    {
        return new PcBillingSaga
        {
            Id = message.PcOrderPlacedId,
            ProductName = message.ProductName
        };
    }

    public void Handle(PcPaymentReceived message)
    {
        MarkCompleted();
    }
}
