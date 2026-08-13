using Fisher;
using JasperFx;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Shouldly;
using Wolverine;
using Wolverine.Fisher;
using Wolverine.Persistence;
using Wolverine.Tracking;

namespace FisherTests;

// The Fisher half of the store agnostic event side effects -- the handler below is character for character
// what the Marten and Polecat suites run.
public class event_side_effects : IAsyncLifetime
{
    private FisherTestDatabase theDatabase = null!;
    private IHost _host = null!;

    public async ValueTask InitializeAsync()
    {
        theDatabase = Servers.CreateDatabase("event_side_effects");

        _host = await Host.CreateDefaultBuilder()
            .UseWolverine(opts =>
            {
                opts.Discovery.DisableConventionalDiscovery().IncludeType(typeof(FiInvoiceHandler));
                opts.Durability.Mode = DurabilityMode.Solo;
                opts.Policies.AutoApplyTransactions();

                opts.Services.AddFisher(m =>
                    {
                        m.Connection(theDatabase.ConnectionString);
                        m.AutoCreateSchemaObjects = AutoCreate.All;
                    })
                    .ApplyAllDatabaseChangesOnStartup()
                    .IntegrateWithWolverine();
            }).StartAsync();
    }

    public async ValueTask DisposeAsync()
    {
        await _host.StopAsync();
        _host.Dispose();
        theDatabase.Dispose();
    }

    [Fact]
    public async Task start_stream_creates_the_stream_and_its_events()
    {
        var id = Guid.NewGuid();

        await _host.InvokeMessageAndWaitAsync(new CreateFiInvoice(id, 100));

        await using var session = _host.Services.GetRequiredService<IDocumentStore>().LightweightSession();
        var events = await session.Events.FetchStreamAsync(id, token: TestContext.Current.CancellationToken);

        events.Count.ShouldBe(1);
        events[0].Data.ShouldBeOfType<FiInvoiceCreated>().Amount.ShouldBe(100);
    }

    [Fact]
    public async Task append_events_adds_to_an_existing_stream()
    {
        var id = Guid.NewGuid();
        await _host.InvokeMessageAndWaitAsync(new CreateFiInvoice(id, 100));

        await _host.InvokeMessageAndWaitAsync(new ApproveFiInvoice(id, "kareem"));

        await using var session = _host.Services.GetRequiredService<IDocumentStore>().LightweightSession();
        var events = await session.Events.FetchStreamAsync(id, token: TestContext.Current.CancellationToken);

        events.Count.ShouldBe(2);
        events[1].Data.ShouldBeOfType<FiInvoiceApproved>().ApprovedBy.ShouldBe("kareem");
    }
}

public record FiInvoiceCreated(decimal Amount);

public record FiInvoiceApproved(string ApprovedBy);

public record CreateFiInvoice(Guid Id, decimal Amount);

public record ApproveFiInvoice(Guid Id, string ApprovedBy);

public static class FiInvoiceHandler
{
    public static StartStream Handle(CreateFiInvoice command)
        => Storage.StartStream(command.Id, new FiInvoiceCreated(command.Amount));

    public static AppendEvents Handle(ApproveFiInvoice command)
        => Storage.AppendEvents(command.Id, new FiInvoiceApproved(command.ApprovedBy));
}
