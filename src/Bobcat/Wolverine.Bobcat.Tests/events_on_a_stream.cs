using IntegrationTests;
using JasperFx;
using JasperFx.Events;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Wolverine.Fisher;
using Wolverine.Marten;
using Wolverine.Persistence.EventSourcing;
using Wolverine.Polecat;
using Fisher;
using Marten;
using Polecat;
using PolecatStore = global::Polecat.IDocumentStore;

namespace Wolverine.Bobcat.Tests.EventsOnAStream;

// GH-4920: a command that decides against two streams appends to each, and ThenEvents can only speak for
// the one the act addresses. ThenEventsOn<T>(id, ...) checks one stream at a time, counting only what the
// act added -- on Marten, Polecat and Fisher.

public record LedgerOpened;
public record LedgerNoted(string Note);
public record JournalOpened;
public record JournalNoted(string Note);

public record NoteBoth(Guid LedgerId, Guid JournalId, string Note);
public record NoteLedgerOnly(Guid LedgerId, Guid JournalId, string Note);

public class Ledger
{
    public Guid Id { get; set; }
    public int Notes { get; set; }
    public void Apply(LedgerOpened e) { }
    public void Apply(LedgerNoted e) => Notes++;
}

public class Journal
{
    public Guid Id { get; set; }
    public int Notes { get; set; }
    public void Apply(JournalOpened e) { }
    public void Apply(JournalNoted e) => Notes++;
}

public static class NoteBothHandler
{
    public static void Handle(NoteBoth command,
        [WriteModel(nameof(NoteBoth.LedgerId))] IEventStream<Ledger> ledger,
        [WriteModel(nameof(NoteBoth.JournalId))] IEventStream<Journal> journal)
    {
        ledger.AppendOne(new LedgerNoted(command.Note));
        journal.AppendOne(new JournalNoted(command.Note));
    }
}

public static class NoteLedgerOnlyHandler
{
    public static void Handle(NoteLedgerOnly command,
        [WriteModel(nameof(NoteLedgerOnly.LedgerId))] IEventStream<Ledger> ledger,
        [WriteModel(nameof(NoteLedgerOnly.JournalId))] IEventStream<Journal> journal)
        => ledger.AppendOne(new LedgerNoted(command.Note));
}

public abstract class TwoStreamHost : IAsyncLifetime
{
    public IHost Host { get; private set; } = null!;

    protected abstract void ConfigureStore(WolverineOptions opts);

    protected virtual Task AfterStartAsync() => Task.CompletedTask;

    public async ValueTask InitializeAsync()
    {
        Host = await Microsoft.Extensions.Hosting.Host.CreateDefaultBuilder()
            .UseWolverine(opts =>
            {
                opts.Durability.Mode = DurabilityMode.Solo;
                opts.Policies.AutoApplyTransactions();
                opts.Discovery.DisableConventionalDiscovery()
                    .IncludeType(typeof(NoteBothHandler))
                    .IncludeType(typeof(NoteLedgerOnlyHandler));
                ConfigureStore(opts);
            })
            .StartAsync();

        await AfterStartAsync();
    }

    public async ValueTask DisposeAsync()
    {
        // A host that failed to start has nothing to stop; let the startup failure be the one reported
        if (Host is null) return;

        await Host.StopAsync();
        Host.Dispose();
    }
}

public sealed class MartenTwoStreamHost : TwoStreamHost
{
    protected override void ConfigureStore(WolverineOptions opts)
        => opts.Services.AddMarten(m =>
        {
            m.Connection(Servers.PostgresConnectionString);
            m.DatabaseSchemaName = "bobcat_events_on_a_stream";
            m.DisableNpgsqlLogging = true;
        }).IntegrateWithWolverine();
}

public sealed class PolecatTwoStreamHost : TwoStreamHost
{
    protected override void ConfigureStore(WolverineOptions opts)
        => opts.Services.AddPolecat(m =>
        {
            m.ConnectionString = Servers.SqlServerConnectionString;
            m.DatabaseSchemaName = "bobcat_events_on_a_stream";
        }).IntegrateWithWolverine();

    protected override Task AfterStartAsync()
        => ((global::Polecat.DocumentStore)Host.Services.GetRequiredService<PolecatStore>()).Database.ApplyAllConfiguredChangesToDatabaseAsync();
}

public sealed class FisherTwoStreamHost : TwoStreamHost
{
    private readonly string _file = Path.Combine(Path.GetTempPath(), $"bobcat_events_on_a_stream_{Guid.NewGuid():N}.db");

    protected override void ConfigureStore(WolverineOptions opts)
        => opts.Services.AddFisher(o =>
            {
                o.Connection($"Data Source={_file}");
                o.AutoCreateSchemaObjects = AutoCreate.All;
            })
            .ApplyAllDatabaseChangesOnStartup()
            .IntegrateWithWolverine();
}

public abstract class events_on_a_stream<THost>(THost app) : WolverineSpec(app.Host)
    where THost : TwoStreamHost
{
    [Fact]
    public async Task each_stream_is_checked_on_its_own_and_arranged_history_never_counts()
    {
        var theLedger = Guid.CreateVersion7();
        var theJournal = Guid.CreateVersion7();
        await GivenEvents<Ledger>(theLedger, new LedgerOpened());
        await GivenEventsOn<Journal>(theJournal, new JournalOpened());

        await WhenReceived(new NoteBoth(theLedger, theJournal, "fostered"));

        ThenEventsOn<Ledger>(theLedger, new LedgerNoted("fostered"));
        ThenEventsOn<Journal>(theJournal, Specify<JournalNoted>().With(x => x.Note, "fostered"));

        // Every stream's share of the act, the one the act addressed and the other alike
        Scenario.LastAct.AllNewEvents.Select(x => x.Data.GetType().Name).ShouldBe(["LedgerNoted", "JournalNoted"], ignoreOrder: true);
    }

    [Fact]
    public async Task no_events_on_a_stream_the_act_left_alone()
    {
        var theLedger = Guid.CreateVersion7();
        var theJournal = Guid.CreateVersion7();
        await GivenEvents<Ledger>(theLedger, new LedgerOpened());
        await GivenEventsOn<Journal>(theJournal, new JournalOpened());

        await WhenReceived(new NoteLedgerOnly(theLedger, theJournal, "fostered"));

        ThenEventsOn<Ledger>(theLedger, new LedgerNoted("fostered"));
        ThenNoEventsOn<Journal>(theJournal);
    }

    [Fact]
    public async Task a_stream_the_act_did_not_append_to_says_where_the_events_went()
    {
        var theLedger = Guid.CreateVersion7();
        var theJournal = Guid.CreateVersion7();
        await GivenEvents<Ledger>(theLedger, new LedgerOpened());
        await GivenEventsOn<Journal>(theJournal, new JournalOpened());

        await WhenReceived(new NoteLedgerOnly(theLedger, theJournal, "fostered"));

        var failure = Should.Throw<Exception>(() => ThenEventsOn<Journal>(theJournal, new JournalNoted("fostered")));
        failure.Message.ShouldContain("appended none there");
        failure.Message.ShouldContain("LedgerNoted");
    }
}

public sealed class marten_events_on_a_stream(MartenTwoStreamHost app)
    : events_on_a_stream<MartenTwoStreamHost>(app), IClassFixture<MartenTwoStreamHost>;

public sealed class polecat_events_on_a_stream(PolecatTwoStreamHost app)
    : events_on_a_stream<PolecatTwoStreamHost>(app), IClassFixture<PolecatTwoStreamHost>;

public sealed class fisher_events_on_a_stream(FisherTwoStreamHost app)
    : events_on_a_stream<FisherTwoStreamHost>(app), IClassFixture<FisherTwoStreamHost>;
