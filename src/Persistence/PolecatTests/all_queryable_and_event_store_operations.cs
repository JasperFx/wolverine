using IntegrationTests;
using JasperFx.Events;
// NB: importing the JasperFx.Events.Documents NAMESPACE here would make ToListAsync() ambiguous
// between DocumentQueryableExtensions and Polecat's own queryable extensions (CS0121), so alias
// just the contracts under test.
using IDocumentSessionOperations = JasperFx.Events.Documents.IDocumentSessionOperations;
using IDocumentWriteOperations = JasperFx.Events.Documents.IDocumentWriteOperations;
using IDocumentReadOperations = JasperFx.Events.Documents.IDocumentReadOperations;
using IDocumentSessionFactory = JasperFx.Events.Documents.IDocumentSessionFactory;
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

    /// <summary>
    ///     GH-3956. The store-agnostic JasperFx.Events.Documents contracts bind AND commit. Before this,
    ///     a handler declaring one failed codegen outright on a stock host; once bound by the variable
    ///     source alone, its writes were queued into the session's unit of work and silently discarded.
    /// </summary>
    [Fact]
    public async Task document_session_operations_parameter_is_committed()
    {
        var id = Guid.NewGuid();

        await _host.InvokeMessageAndWaitAsync(new StorePcNote(id, "session-ops"));

        await using var session = _host.Services.GetRequiredService<IDocumentStore>().LightweightSession();
        var note = await session.LoadAsync<PcNote>(id, TestContext.Current.CancellationToken);

        note.ShouldNotBeNull();
        note.Text.ShouldBe("session-ops");
    }

    /// <summary>
    ///     The read and write contracts must be satisfied by the same session variable, or a handler that
    ///     writes and then reads would silently be reading from a different unit of work.
    /// </summary>
    [Fact]
    public async Task read_and_write_operations_resolve_to_one_session()
    {
        var result = await _host.MessageBus()
            .InvokeAsync<PcSessionsCompared>(new ComparePcSessions(), TestContext.Current.CancellationToken);

        result.SameInstance.ShouldBeTrue();
    }

    /// <summary>
    ///     Polecat registers IDocumentStore, never the lifted factory contract it already implements.
    /// </summary>
    [Fact]
    public void document_session_factory_contracts_are_registered()
    {
        var store = _host.Services.GetRequiredService<IDocumentStore>();
        _host.Services.GetRequiredService<IDocumentSessionFactory>().ShouldBeSameAs(store);
        _host.Services
            .GetRequiredService<JasperFx.Events.Documents.IDocumentSessionFactory<IDocumentSession, IQuerySession>>()
            .ShouldBeSameAs(store);
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
public class PcNote
{
    public Guid Id { get; set; }
    public string Text { get; set; } = string.Empty;
}

public record StorePcNote(Guid Id, string Text);
public record ComparePcSessions;
public record PcSessionsCompared(bool SameInstance);

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

    // Only JasperFx.Events.Documents types named here -- nothing Polecat-specific
    public static void Handle(StorePcNote command, IDocumentSessionOperations session)
        => session.Store(new PcNote { Id = command.Id, Text = command.Text });

    public static PcSessionsCompared Handle(ComparePcSessions command,
        IDocumentWriteOperations writes, IDocumentReadOperations reads)
        => new(ReferenceEquals(writes, reads));
}
