using JasperFx.Events;
using Fisher;
using JasperFx;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Shouldly;
using Wolverine;
using Wolverine.Configuration.EventModeling;
using Wolverine.Fisher;
using Wolverine.Persistence.EventSourcing;
using Wolverine.Tracking;

namespace FisherTests.ScaffoldedShapes;

// The handler shapes `wolverine scaffold` writes, run for real on Fisher. The same suite runs on
// Marten (MartenTests) and Polecat (PolecatTests): the scaffold is store-agnostic, so its shapes have
// to hold on every store.
//
//   GH-4889: TEvent? Handle(command, [WriteModel] Aggregate) -- one event, or null for none, with no
//            [Emits], and the derived Event Model still knows what it emits.
//   GH-4914: no shape carries [Emits]; the source generator reads what each body appends.
//   GH-4889: [WriteModel] Aggregate? -- the stream may not exist yet; the event starts it.
//   GH-4895: [WriteModel(nameof(Command.XId))] IEventStream<X> per stream -- a command that decides
//            against several streams, the same aggregate type twice included.
//   GH-4892: FisherOps.StartStream(id, events) -- a stream with no aggregate type.
public class scaffolded_handler_shapes : IAsyncLifetime
{
    private FisherTestDatabase theDatabase = null!;
    private IHost theHost = null!;

    public async ValueTask InitializeAsync()
    {
        theDatabase = Servers.CreateDatabase(nameof(scaffolded_handler_shapes));

        theHost = await Host.CreateDefaultBuilder()
            .UseWolverine(opts =>
            {
                opts.ServiceName = "scaffolded-handler-shapes-fisher";
                opts.Discovery.DisableConventionalDiscovery()
                    .IncludeType(typeof(NoteLedgerHandler))
                    .IncludeType(typeof(OpenLedgerHandler))
                    .IncludeType(typeof(StartUntypedLedgerHandler))
                    .IncludeType(typeof(NoteLedgerAndJournalHandler))
                    .IncludeType(typeof(TransferNoteHandler));

                opts.Durability.Mode = DurabilityMode.Solo;
                opts.Policies.AutoApplyTransactions();
                opts.Services.AddFisher(o =>
                    {
                        o.Connection(theDatabase.ConnectionString);
                        o.AutoCreateSchemaObjects = AutoCreate.All;
                    })
                    .ApplyAllDatabaseChangesOnStartup()
                    .IntegrateWithWolverine();
            }).StartAsync();
    }

    public async ValueTask DisposeAsync()
    {
        await theHost.StopAsync();
        theHost.Dispose();
        theDatabase.Dispose();
    }

    private async Task<Guid> givenLedger()
    {
        var id = Guid.CreateVersion7();
        await using var session = theHost.Services.GetRequiredService<IDocumentStore>().LightweightSession();
        session.Events.StartStream<Ledger>(id, new LedgerOpened());
        await session.SaveChangesAsync(TestContext.Current.CancellationToken);
        return id;
    }

    private async Task<Guid> givenJournal()
    {
        var id = Guid.CreateVersion7();
        await using var session = theHost.Services.GetRequiredService<IDocumentStore>().LightweightSession();
        session.Events.StartStream<Journal>(id, new JournalOpened());
        await session.SaveChangesAsync(TestContext.Current.CancellationToken);
        return id;
    }

    private async Task<IReadOnlyList<IEvent>> streamOf(Guid id)
    {
        await using var session = theHost.Services.GetRequiredService<IDocumentStore>().LightweightSession();
        return await session.Events.FetchStreamAsync(id, token: TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task a_returned_event_is_appended_to_the_loaded_stream()
    {
        var id = await givenLedger();

        await theHost.InvokeMessageAndWaitAsync(new NoteLedger(id, "called back"));

        var events = await streamOf(id);
        events.Count.ShouldBe(2);
        events[1].Data.ShouldBeOfType<LedgerNoted>().Note.ShouldBe("called back");
    }

    [Fact]
    public async Task a_returned_null_appends_nothing()
    {
        var id = await givenLedger();

        await theHost.InvokeMessageAndWaitAsync(new NoteLedger(id, null));

        (await streamOf(id)).Count.ShouldBe(1);
    }

    [Fact]
    public async Task a_nullable_write_model_starts_the_stream_when_it_does_not_exist_yet()
    {
        var id = Guid.CreateVersion7();

        await theHost.InvokeMessageAndWaitAsync(new OpenLedger(id));
        await theHost.InvokeMessageAndWaitAsync(new OpenLedger(id));

        var events = await streamOf(id);
        events.Count.ShouldBe(1);
        events[0].Data.ShouldBeOfType<LedgerOpened>();
    }

    [Fact]
    public async Task an_untyped_start_stream_has_no_aggregate_type()
    {
        var id = Guid.CreateVersion7();

        await theHost.InvokeMessageAndWaitAsync(new StartUntypedLedger(id));

        (await streamOf(id)).Count.ShouldBe(1);
        await using var session = theHost.Services.GetRequiredService<IDocumentStore>().LightweightSession();
        var state = await session.Events.FetchStreamStateAsync(id, TestContext.Current.CancellationToken);
        state.ShouldNotBeNull().AggregateType.ShouldBeNull();
    }

    [Fact]
    public async Task each_event_stream_gets_its_own_appends()
    {
        var ledger = await givenLedger();
        var journal = await givenJournal();

        await theHost.InvokeMessageAndWaitAsync(new NoteLedgerAndJournal(ledger, journal, "both"));

        var ledgerEvents = await streamOf(ledger);
        ledgerEvents.Count.ShouldBe(2);
        ledgerEvents[1].Data.ShouldBeOfType<LedgerNoted>().Note.ShouldBe("both");

        var journalEvents = await streamOf(journal);
        journalEvents.Count.ShouldBe(2);
        journalEvents[1].Data.ShouldBeOfType<JournalNoted>().Note.ShouldBe("both");
    }

    [Fact]
    public async Task two_streams_of_the_same_aggregate_type_each_get_their_own_appends()
    {
        var from = await givenLedger();
        var to = await givenLedger();

        await theHost.InvokeMessageAndWaitAsync(new TransferNote(from, to, "moved"));

        (await streamOf(from))[1].Data.ShouldBeOfType<LedgerNoted>().Note.ShouldBe("moved out");
        (await streamOf(to))[1].Data.ShouldBeOfType<LedgerNoted>().Note.ShouldBe("moved in");
    }

    [Fact]
    public void the_derived_event_model_reads_a_nullable_event_return_with_no_emits_attribute()
    {
        var model = WolverineEventModelSource.Describe(theHost.GetRuntime());

        var noted = model.Slices.Single(x => x.Name == nameof(NoteLedger));
        noted.AggregateTypes.Select(x => x.Name).ShouldBe(new[] { nameof(Ledger) });
        noted.EmittedEvents.Select(x => x.Name).ShouldBe(new[] { nameof(LedgerNoted) });

        model.Slices.Single(x => x.Name == nameof(OpenLedger))
            .EmittedEvents.Select(x => x.Name).ShouldBe(new[] { nameof(LedgerOpened) });
    }

    [Fact]
    public void the_derived_event_model_reads_what_a_handler_body_appends_with_no_emits_attribute()
    {
        // GH-4914: JasperFx.Events.SourceGenerator reads the events these shapes construct in their bodies
        // into the assembly's emitted-events manifest (jasperfx#990), so none of them carries [Emits]
        var model = WolverineEventModelSource.Describe(theHost.GetRuntime());

        string[] emitted(string slice) => model.Slices.Single(x => x.Name == slice).EmittedEvents.Select(x => x.Name).OrderBy(x => x).ToArray();

        emitted(nameof(StartUntypedLedger)).ShouldBe(new[] { nameof(LedgerOpened) });
        emitted(nameof(NoteLedgerAndJournal)).ShouldBe(new[] { nameof(JournalNoted), nameof(LedgerNoted) });
        emitted(nameof(TransferNote)).ShouldBe(new[] { nameof(LedgerNoted) });
    }
}

public record LedgerOpened;

public record LedgerNoted(string Note);

public record NoteLedger(Guid Id, string? Note);

public record OpenLedger(Guid Id);

public record StartUntypedLedger(Guid Id);

public record JournalOpened;

public record JournalNoted(string Note);

public record NoteLedgerAndJournal(Guid LedgerId, Guid JournalId, string Note);

public record TransferNote(Guid FromId, Guid ToId, string Note);

public class Journal
{
    public Guid Id { get; set; }

    public void Apply(JournalOpened e)
    {
    }

    public void Apply(JournalNoted e)
    {
    }
}

public class Ledger
{
    public Guid Id { get; set; }
    public int Notes { get; set; }

    public void Apply(LedgerOpened e)
    {
    }

    public void Apply(LedgerNoted e) => Notes++;
}

public static class NoteLedgerHandler
{
    public static LedgerNoted? Handle(NoteLedger command, [WriteModel] Ledger ledger)
        => command.Note is null ? null : new LedgerNoted(command.Note);
}

public static class OpenLedgerHandler
{
    public static LedgerOpened? Handle(OpenLedger command, [WriteModel] Ledger? ledger)
        => ledger is null ? new LedgerOpened() : null;
}

public static class StartUntypedLedgerHandler
{
    public static StartStreamWithoutAggregate Handle(StartUntypedLedger command)
        => FisherOps.StartStream(command.Id, new LedgerOpened());
}

// GH-4895: exactly the shape the scaffold writes for a command against several aggregates
public static class NoteLedgerAndJournalHandler
{
    public static void Handle(NoteLedgerAndJournal command,
        [WriteModel(nameof(NoteLedgerAndJournal.LedgerId))] IEventStream<Ledger> ledgerStream,
        [WriteModel(nameof(NoteLedgerAndJournal.JournalId))] IEventStream<Journal> journalStream)
    {
        ledgerStream.AppendOne(new LedgerNoted(command.Note));
        journalStream.AppendOne(new JournalNoted(command.Note));
    }
}

// The same aggregate type twice: each stream is told apart by the member that identifies it
public static class TransferNoteHandler
{
    public static void Handle(TransferNote command,
        [WriteModel(nameof(TransferNote.FromId))] IEventStream<Ledger> from,
        [WriteModel(nameof(TransferNote.ToId))] IEventStream<Ledger> to)
    {
        from.AppendOne(new LedgerNoted(command.Note + " out"));
        to.AppendOne(new LedgerNoted(command.Note + " in"));
    }
}
