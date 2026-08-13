using IntegrationTests;
using JasperFx.Events;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Polecat;
using Polecat.Linq;
using Shouldly;
using Wolverine;
using Wolverine.Attributes;
using Wolverine.Persistence;
using Wolverine.Polecat;
using Wolverine.Tracking;

namespace PolecatTests;

// The Polecat proof for [All], [Queryable] and the IEventStoreOperations parameter. Deliberately one test
// class rather than three: the Polecat CI shards are balanced by test-CLASS count because the per-class
// Wolverine + Polecat + SqlServer fixture cost dominates, so three classes here would cost three bootstraps
// to assert what one can.
public class all_queryable_and_event_store_operations : IAsyncLifetime
{
    private IHost _host = null!;

    public async ValueTask InitializeAsync()
    {
        _host = await Host.CreateDefaultBuilder()
            .UseWolverine(opts =>
            {
                opts.Discovery.DisableConventionalDiscovery().IncludeType(typeof(PcCatalogHandler));
                opts.Durability.Mode = DurabilityMode.Solo;
                opts.Policies.AutoApplyTransactions();
                opts.Services.AddPolecat(m =>
                {
                    m.ConnectionString = Servers.SqlServerConnectionString;
                    m.DatabaseSchemaName = "pc_all_queryable";
                }).IntegrateWithWolverine();
            }).StartAsync();

        var store = (DocumentStore)_host.Services.GetRequiredService<IDocumentStore>();
        await store.Database.ApplyAllConfiguredChangesToDatabaseAsync();
        await store.Advanced.Clean.DeleteAllDocumentsAsync();
    }

    public async ValueTask DisposeAsync()
    {
        await _host.StopAsync();
        _host.Dispose();
    }

    private async Task seed()
    {
        await using var session = _host.Services.GetRequiredService<IDocumentStore>().LightweightSession();
        session.Store(new PcWidget { Name = "red", Hits = 5 });
        session.Store(new PcWidget { Name = "green", Hits = 12 });
        session.Store(new PcWidget { Name = "blue", Hits = 3 });
        await session.SaveChangesAsync(TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task all_gives_an_empty_list_when_nothing_is_stored()
    {
        var tracked = await _host.InvokeMessageAndWaitAsync(new CountPcWidgets());
        tracked.Sent.SingleMessage<PcWidgetsCounted>().Count.ShouldBe(0);
    }

    [Fact]
    public async Task all_supplies_every_document()
    {
        await seed();
        var tracked = await _host.InvokeMessageAndWaitAsync(new CountPcWidgets());
        tracked.Sent.SingleMessage<PcWidgetsCounted>().Count.ShouldBe(3);
    }

    [Fact]
    public async Task queryable_can_be_composed_against()
    {
        await seed();
        var tracked = await _host.InvokeMessageAndWaitAsync(new FindPopularPcWidgets(4));
        tracked.Sent.SingleMessage<PopularPcWidgetsFound>().Names.ShouldBe(["green", "red"]);
    }

    [Fact]
    public async Task event_store_operations_parameter_is_the_current_sessions_events()
    {
        var id = Guid.NewGuid();

        await _host.InvokeMessageAndWaitAsync(new RecordPcLedgerEntry(id, "opening"));

        await using var session = _host.Services.GetRequiredService<IDocumentStore>().LightweightSession();
        var events = await session.Events.FetchStreamAsync(id, token: TestContext.Current.CancellationToken);

        events.Count.ShouldBe(1);
        events[0].Data.ShouldBeOfType<PcLedgerEntryRecorded>().Note.ShouldBe("opening");
    }
}

public class PcWidget
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string Name { get; set; } = null!;
    public int Hits { get; set; }
}

public record CountPcWidgets;
public record FindPopularPcWidgets(int Minimum);
public record PcWidgetsCounted(int Count);
public record PopularPcWidgetsFound(string[] Names);
public record PcLedgerEntryRecorded(string Note);
public record RecordPcLedgerEntry(Guid Id, string Note);

[WolverineIgnore]
public static class PcCatalogHandler
{
    public static PcWidgetsCounted Handle(CountPcWidgets command, [All] IReadOnlyList<PcWidget> widgets)
        => new(widgets.Count);

    // Async LINQ only -- see the [Queryable] warnings
    public static async Task<PopularPcWidgetsFound> Handle(FindPopularPcWidgets command,
        [Queryable] IQueryable<PcWidget> widgets, CancellationToken token)
    {
        var names = await widgets.Where(x => x.Hits >= command.Minimum)
            .OrderByDescending(x => x.Hits)
            .Select(x => x.Name)
            .ToListAsync(token);

        return new PopularPcWidgetsFound(names.ToArray());
    }

    public static void Handle(RecordPcLedgerEntry command, IEventStoreOperations events)
        => events.StartStream(command.Id, new PcLedgerEntryRecorded(command.Note));

    public static void Handle(PcWidgetsCounted msg) { }
    public static void Handle(PopularPcWidgetsFound msg) { }
}
