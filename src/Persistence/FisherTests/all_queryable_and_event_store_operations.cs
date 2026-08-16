using JasperFx;
using JasperFx.Events;
// NB: importing the JasperFx.Events.Documents NAMESPACE here would make ToListAsync() ambiguous
// between DocumentQueryableExtensions and Fisher's own queryable extensions (CS0121), so alias
// just the contracts under test.
using IDocumentSessionOperations = JasperFx.Events.Documents.IDocumentSessionOperations;
using IDocumentWriteOperations = JasperFx.Events.Documents.IDocumentWriteOperations;
using IDocumentReadOperations = JasperFx.Events.Documents.IDocumentReadOperations;
using IDocumentSessionFactory = JasperFx.Events.Documents.IDocumentSessionFactory;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Fisher;
using Fisher.Linq;
using Shouldly;
using Wolverine;
using Wolverine.Attributes;
using Wolverine.Persistence;
using Wolverine.Fisher;
using Wolverine.Tracking;

namespace FisherTests;

// The Fisher proof for [All], [Queryable] and the IEventStoreOperations parameter. Deliberately one test
// class rather than three: the Fisher suite are balanced by test-CLASS count because the per-class
// Wolverine + Fisher + SQLite fixture cost dominates, so three classes here would cost three bootstraps
// to assert what one can.
public class all_queryable_and_event_store_operations : IAsyncLifetime
{
    private FisherTestDatabase theDatabase = null!;
    private IHost _host = null!;

    public async ValueTask InitializeAsync()
    {
        theDatabase = Servers.CreateDatabase("all_queryable");

        _host = await Host.CreateDefaultBuilder()
            .UseWolverine(opts =>
            {
                opts.Discovery.DisableConventionalDiscovery().IncludeType(typeof(FiCatalogHandler));
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

    private async Task seed()
    {
        await using var session = _host.Services.GetRequiredService<IDocumentStore>().LightweightSession();
        session.Store(new FiWidget { Name = "red", Hits = 5 });
        session.Store(new FiWidget { Name = "green", Hits = 12 });
        session.Store(new FiWidget { Name = "blue", Hits = 3 });
        await session.SaveChangesAsync(TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task all_gives_an_empty_list_when_nothing_is_stored()
    {
        // Fisher creates a document table lazily on first write, and querying a type that has never been
        // written throws "no such table" rather than returning nothing. That is a general Fisher trait, not
        // something [All] introduces -- so establish the table then empty it, which is the state an
        // application is actually in once it has used the type at all.
        await using (var session = _host.Services.GetRequiredService<IDocumentStore>().LightweightSession())
        {
            var seed = new FiWidget { Name = "temp", Hits = 1 };
            session.Store(seed);
            await session.SaveChangesAsync(TestContext.Current.CancellationToken);
            session.Delete(seed);
            await session.SaveChangesAsync(TestContext.Current.CancellationToken);
        }

        var tracked = await _host.InvokeMessageAndWaitAsync(new CountFiWidgets());
        tracked.Sent.SingleMessage<FiWidgetsCounted>().Count.ShouldBe(0);
    }

    [Fact]
    public async Task all_supplies_every_document()
    {
        await seed();
        var tracked = await _host.InvokeMessageAndWaitAsync(new CountFiWidgets());
        tracked.Sent.SingleMessage<FiWidgetsCounted>().Count.ShouldBe(3);
    }

    [Fact]
    public async Task queryable_can_be_composed_against()
    {
        await seed();
        var tracked = await _host.InvokeMessageAndWaitAsync(new FindPopularFiWidgets(4));
        tracked.Sent.SingleMessage<PopularFiWidgetsFound>().Names.ShouldBe(["green", "red"]);
    }

    [Fact]
    public async Task event_store_operations_parameter_is_the_current_sessions_events()
    {
        var id = Guid.NewGuid();

        await _host.InvokeMessageAndWaitAsync(new RecordFiLedgerEntry(id, "opening"));

        await using var session = _host.Services.GetRequiredService<IDocumentStore>().LightweightSession();
        var events = await session.Events.FetchStreamAsync(id, token: TestContext.Current.CancellationToken);

        events.Count.ShouldBe(1);
        events[0].Data.ShouldBeOfType<FiLedgerEntryRecorded>().Note.ShouldBe("opening");
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

        await _host.InvokeMessageAndWaitAsync(new StoreFiNote(id, "session-ops"));

        await using var session = _host.Services.GetRequiredService<IDocumentStore>().LightweightSession();
        var note = await session.LoadAsync<FiNote>(id, TestContext.Current.CancellationToken);

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
            .InvokeAsync<FiSessionsCompared>(new CompareFiSessions(), TestContext.Current.CancellationToken);

        result.SameInstance.ShouldBeTrue();
    }

    /// <summary>
    ///     Fisher registers IDocumentStore, never the lifted factory contract it already implements.
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
public class FiWidget
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string Name { get; set; } = null!;
    public int Hits { get; set; }
}

public record CountFiWidgets;
public record FindPopularFiWidgets(int Minimum);
public record FiWidgetsCounted(int Count);
public record PopularFiWidgetsFound(string[] Names);
public record FiLedgerEntryRecorded(string Note);
public record RecordFiLedgerEntry(Guid Id, string Note);

[WolverineIgnore]
public class FiNote
{
    public Guid Id { get; set; }
    public string Text { get; set; } = string.Empty;
}

public record StoreFiNote(Guid Id, string Text);
public record CompareFiSessions;
public record FiSessionsCompared(bool SameInstance);

[WolverineIgnore]
public static class FiCatalogHandler
{
    public static FiWidgetsCounted Handle(CountFiWidgets command, [All] IReadOnlyList<FiWidget> widgets)
        => new(widgets.Count);

    // Async LINQ only -- see the [Queryable] warnings
    public static async Task<PopularFiWidgetsFound> Handle(FindPopularFiWidgets command,
        [Queryable] IQueryable<FiWidget> widgets, CancellationToken token)
    {
        var names = await widgets.Where(x => x.Hits >= command.Minimum)
            .OrderByDescending(x => x.Hits)
            .Select(x => x.Name)
            .ToListAsync(token);

        return new PopularFiWidgetsFound(names.ToArray());
    }

    public static void Handle(RecordFiLedgerEntry command, IEventStoreOperations events)
        => events.StartStream(command.Id, new FiLedgerEntryRecorded(command.Note));

    public static void Handle(FiWidgetsCounted msg) { }
    public static void Handle(PopularFiWidgetsFound msg) { }

    // Only JasperFx.Events.Documents types named here -- nothing Fisher-specific
    public static void Handle(StoreFiNote command, IDocumentSessionOperations session)
        => session.Store(new FiNote { Id = command.Id, Text = command.Text });

    public static FiSessionsCompared Handle(CompareFiSessions command,
        IDocumentWriteOperations writes, IDocumentReadOperations reads)
        => new(ReferenceEquals(writes, reads));
}
