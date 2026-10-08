using IntegrationTests;
using JasperFx.Events;
using Marten;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Shouldly;
using Wolverine;
using Wolverine.Configuration.EventModeling;
using Wolverine.Marten;
using Wolverine.Persistence.EventSourcing;
using Wolverine.Tracking;

namespace MartenTests.EventModeling.ScaffoldedShapes;

// The handler shapes `wolverine scaffold` writes, run for real on Marten. The same suite runs on
// Polecat (PolecatTests) and Fisher (FisherTests): the scaffold is store-agnostic, so its shapes have
// to hold on every store.
//
//   GH-4889: TEvent? Handle(command, [WriteModel] Aggregate) -- one event, or null for none, with no
//            [Emits], and the derived Event Model still knows what it emits.
//   GH-4889: [WriteModel] Aggregate? -- the stream may not exist yet; the event starts it.
//   GH-4892: MartenOps.StartStream(id, events) -- a stream with no aggregate type.
public class scaffolded_handler_shapes : IAsyncLifetime
{
    private IHost theHost = null!;

    public async ValueTask InitializeAsync()
    {
        theHost = await Host.CreateDefaultBuilder()
            .UseWolverine(opts =>
            {
                opts.ServiceName = "scaffolded-handler-shapes";
                opts.Discovery.DisableConventionalDiscovery()
                    .IncludeType(typeof(NoteLedgerHandler))
                    .IncludeType(typeof(OpenLedgerHandler))
                    .IncludeType(typeof(StartUntypedLedgerHandler));

                opts.Durability.Mode = DurabilityMode.Solo;
                opts.Policies.AutoApplyTransactions();
                opts.Services.AddMarten(m =>
                {
                    m.Connection(Servers.PostgresConnectionString);
                    m.DatabaseSchemaName = "scaffolded_handler_shapes";
                    m.DisableNpgsqlLogging = true;
                }).IntegrateWithWolverine();
            }).StartAsync();
    }

    public async ValueTask DisposeAsync()
    {
        await theHost.StopAsync();
        theHost.Dispose();
    }

    private async Task<Guid> givenLedger()
    {
        var id = Guid.CreateVersion7();
        await using var session = theHost.DocumentStore().LightweightSession();
        session.Events.StartStream<Ledger>(id, new LedgerOpened());
        await session.SaveChangesAsync(TestContext.Current.CancellationToken);
        return id;
    }

    private async Task<IReadOnlyList<IEvent>> streamOf(Guid id)
    {
        await using var session = theHost.DocumentStore().LightweightSession();
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
        await using var session = theHost.DocumentStore().LightweightSession();
        var state = await session.Events.FetchStreamStateAsync(id, TestContext.Current.CancellationToken);
        state.ShouldNotBeNull().AggregateType.ShouldBeNull();
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
}

public record LedgerOpened;

public record LedgerNoted(string Note);

public record NoteLedger(Guid Id, string? Note);

public record OpenLedger(Guid Id);

public record StartUntypedLedger(Guid Id);

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
    [Emits(typeof(LedgerOpened))]
    public static StartStreamWithoutAggregate Handle(StartUntypedLedger command)
        => MartenOps.StartStream(command.Id, new LedgerOpened());
}
