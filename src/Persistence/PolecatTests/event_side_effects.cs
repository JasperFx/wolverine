using IntegrationTests;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Polecat;
using Shouldly;
using Wolverine;
using Wolverine.Persistence;
using Wolverine.Polecat;
using Wolverine.Tracking;

namespace PolecatTests;

// The Polecat half of the store agnostic event side effects -- the handler below is character for character
// what the Marten and Fisher suites run.
public class event_side_effects : IAsyncLifetime
{
    private IHost _host = null!;

    public async ValueTask InitializeAsync()
    {
        _host = await Host.CreateDefaultBuilder()
            .UseWolverine(opts =>
            {
                opts.Discovery.DisableConventionalDiscovery().IncludeType(typeof(PcInvoiceHandler));
                opts.Durability.Mode = DurabilityMode.Solo;
                opts.Policies.AutoApplyTransactions();
                opts.Services.AddPolecat(m =>
                {
                    m.ConnectionString = Servers.SqlServerConnectionString;
                    m.DatabaseSchemaName = "pc_event_side_effects";
                }).IntegrateWithWolverine();
            }).StartAsync();

        var store = (DocumentStore)_host.Services.GetRequiredService<IDocumentStore>();
        await store.Database.ApplyAllConfiguredChangesToDatabaseAsync();
    }

    public async ValueTask DisposeAsync()
    {
        await _host.StopAsync();
        _host.Dispose();
    }

    [Fact]
    public async Task start_stream_creates_the_stream_and_its_events()
    {
        var id = Guid.NewGuid();

        await _host.InvokeMessageAndWaitAsync(new CreatePcInvoice(id, 100));

        await using var session = _host.Services.GetRequiredService<IDocumentStore>().LightweightSession();
        var events = await session.Events.FetchStreamAsync(id, token: TestContext.Current.CancellationToken);

        events.Count.ShouldBe(1);
        events[0].Data.ShouldBeOfType<PcInvoiceCreated>().Amount.ShouldBe(100);
    }

    [Fact]
    public async Task append_events_adds_to_an_existing_stream()
    {
        var id = Guid.NewGuid();
        await _host.InvokeMessageAndWaitAsync(new CreatePcInvoice(id, 100));

        await _host.InvokeMessageAndWaitAsync(new ApprovePcInvoice(id, "kareem"));

        await using var session = _host.Services.GetRequiredService<IDocumentStore>().LightweightSession();
        var events = await session.Events.FetchStreamAsync(id, token: TestContext.Current.CancellationToken);

        events.Count.ShouldBe(2);
        events[1].Data.ShouldBeOfType<PcInvoiceApproved>().ApprovedBy.ShouldBe("kareem");
    }
}

public record PcInvoiceCreated(decimal Amount);

public record PcInvoiceApproved(string ApprovedBy);

public record CreatePcInvoice(Guid Id, decimal Amount);

public record ApprovePcInvoice(Guid Id, string ApprovedBy);

public static class PcInvoiceHandler
{
    public static StartStream Handle(CreatePcInvoice command)
        => Storage.StartStream(command.Id, new PcInvoiceCreated(command.Amount));

    public static AppendEvents Handle(ApprovePcInvoice command)
        => Storage.AppendEvents(command.Id, new PcInvoiceApproved(command.ApprovedBy));
}
