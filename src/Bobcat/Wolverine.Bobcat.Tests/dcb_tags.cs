using Bobcat.Engine;
using IntegrationTests;
using JasperFx.Events;
using JasperFx.Events.Tags;
using Marten;
using Marten.Events;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Npgsql;
using Wolverine.Marten;
using Wolverine.Persistence.EventSourcing;

namespace Wolverine.Bobcat.Tests.DcbTags;

// wolverine#4865: a DCB decision appends its events under the tags of the query it decided against, and a
// spec says which: Specify<T>().Tagged(...) or Tagged(event, ...), an exact, unordered match. The tags come
// from what the act's session committed (GH-4931) -- no store reads them back.

public readonly record struct ScreeningId(Guid Value);

public record CustomerId(string Value);

public record ScreeningScheduled(ScreeningId Screening, int Seats);

public record SeatReserved(ScreeningId Screening, CustomerId Customer, string Seat);

public record ReserveSeat(ScreeningId Screening, CustomerId Customer, string Seat);

// GH-4940: tagged with the screening, but not an event type the seat decision reads
public record ScreeningRenamed(ScreeningId Screening, string Title);

public partial class SeatAvailability
{
    // Satisfies the single-stream projection shape FetchForWritingByTags resolves its aggregator through
    public Guid Id { get; set; }
    public int Seats { get; private set; }

    public void Apply(ScreeningScheduled e) => Seats = e.Seats;
    public void Apply(SeatReserved e) => Seats--;
}

public static class ReserveSeatHandler
{
    public static EventTagQuery Load(ReserveSeat command)
        => EventTagQuery.For(command.Screening).AndEventsOfType<ScreeningScheduled, SeatReserved>();

    public static SeatReserved Handle(ReserveSeat command, [DcbModel] SeatAvailability availability)
        => availability.Seats > 0
            ? new SeatReserved(command.Screening, command.Customer, command.Seat)
            : throw new InvalidOperationException("The screening is sold out");
}

public sealed class DcbHost : IAsyncLifetime
{
    public IHost Host { get; private set; } = null!;

    public async ValueTask InitializeAsync()
    {
        await using (var conn = new NpgsqlConnection(Servers.PostgresConnectionString))
        {
            await conn.OpenAsync();
            await using var drop = conn.CreateCommand();
            drop.CommandText = "DROP SCHEMA IF EXISTS bobcat_dcb_tags CASCADE;";
            await drop.ExecuteNonQueryAsync();
        }

        Host = await Microsoft.Extensions.Hosting.Host.CreateDefaultBuilder()
            .UseWolverine(opts =>
            {
                opts.Durability.Mode = DurabilityMode.Solo;
                opts.Discovery.DisableConventionalDiscovery().IncludeType(typeof(ReserveSeatHandler));
                opts.Services.AddMarten(m =>
                    {
                        m.Connection(Servers.PostgresConnectionString);
                        m.DatabaseSchemaName = "bobcat_dcb_tags";
                        m.DisableNpgsqlLogging = true;

                        m.Events.RegisterTagType<ScreeningId>("screening").ForAggregate<SeatAvailability>();
                        m.Events.RegisterTagType<CustomerId>("customer");
                        m.Projections.LiveStreamAggregation<SeatAvailability>();
                    })
                    .UseLightweightSessions()
                    .IntegrateWithWolverine();
            })
            .StartAsync();

        await Host.Services.GetRequiredService<IDocumentStore>().Storage.ApplyAllConfiguredChangesToDatabaseAsync();
    }

    public async ValueTask DisposeAsync()
    {
        if (Host is null) return;
        await Host.StopAsync();
        Host.Dispose();
    }
}

public class dcb_tags(DcbHost app) : WolverineSpec(app.Host), IClassFixture<DcbHost>
{
    private readonly ScreeningId theScreeningId = new(Guid.NewGuid());
    private readonly CustomerId theCustomerId = new("CUST-104");

    [Fact]
    public async Task the_tags_an_event_was_appended_with_are_judged_exactly_in_any_order()
    {
        await GivenEvents<SeatAvailability>(theScreeningId.Value, Tagged(new ScreeningScheduled(theScreeningId, 10), theScreeningId));

        await WhenReceived(new ReserveSeat(theScreeningId, theCustomerId, "4C"));

        ThenEvents(Specify<SeatReserved>().With(x => x.Seat, "4C").Tagged(theCustomerId, theScreeningId));
        ThenEvents(Tagged(new SeatReserved(theScreeningId, theCustomerId, "4C"), theScreeningId, theCustomerId));
        ThenEmitted(Specify<SeatReserved>().Tagged(theScreeningId, theCustomerId));
        ThenNotEmitted(Specify<SeatReserved>().Tagged(new ScreeningId(Guid.NewGuid()), theCustomerId));

        // Exact: a tag left out is as wrong as a wrong one
        var failure = Should.Throw<Exception>(() => ThenEvents(Specify<SeatReserved>().Tagged(theScreeningId)));
        failure.Message.ShouldContain("tags");
    }

    [Fact]
    public async Task an_events_tags_read_on_its_one_row_by_the_names_the_spec_gave_them()
    {
        await GivenEvents<SeatAvailability>(theScreeningId.Value, Tagged(new ScreeningScheduled(theScreeningId, 10), theScreeningId));

        var recording = await Recordings.RecordAsync(async () =>
        {
            await WhenReceived(new ReserveSeat(theScreeningId, theCustomerId, "4C"));
            ThenEvents(Specify<SeatReserved>().With(x => x.Seat, "4C").Tagged(theScreeningId, theCustomerId));
        });

        recording.GatheredFailures().ShouldBeNull();
        var values = recording.Steps.Last().Cells.Single(x => x.Name == "values");
        values.DisplayText.ShouldBe("Seat: 4C, tags: [theScreeningId, CustomerId(CUST-104)]");
    }

    [Fact]
    public async Task a_partial_given_keeps_its_tags_and_the_dcb_model_is_projected_over_a_tag_query()
    {
        var recording = await Recordings.RecordAsync(async () =>
        {
            // A partial arranged event names its tags as an expected one does, and is appended tagged
            await GivenEvents<SeatAvailability>(theScreeningId.Value,
                Specify<ScreeningScheduled>().With(x => x.Screening, theScreeningId).With(x => x.Seats, 2).Tagged(theScreeningId));

            await WhenReceived(new ReserveSeat(theScreeningId, theCustomerId, "4C"));

            await ThenDcbModel<SeatAvailability>(EventTagQuery.For(theScreeningId), Specify<SeatAvailability>().With(x => x.Seats, 1));
        });

        recording.GatheredFailures().ShouldBeNull();
        recording.Steps[0].Text.ShouldBe("SeatAvailability theScreeningId has already recorded ScreeningScheduled(Screening: theScreeningId, Seats: 2) tagged [theScreeningId]");
        recording.Steps.Last().Text.ShouldBe("the SeatAvailability over events tagged theScreeningId matches");

        (await TheDcbModel<SeatAvailability>(EventTagQuery.For(theScreeningId)))!.Seats.ShouldBe(1);
    }

    [Fact]
    public async Task events_are_checked_within_the_boundary_a_tag_query_draws()
    {
        var otherCustomer = new CustomerId("CUST-999");
        await GivenEvents<SeatAvailability>(theScreeningId.Value, Tagged(new ScreeningScheduled(theScreeningId, 10), theScreeningId));

        await WhenReceived(new ReserveSeat(theScreeningId, theCustomerId, "4C"));

        ThenEventsOn(EventTagQuery.For(theCustomerId), Specify<SeatReserved>().With(x => x.Seat, "4C"));
        ThenEventsOn(EventTagQuery.For(theScreeningId).AndEventsOfType<SeatReserved>(), Specify<SeatReserved>().Tagged(theScreeningId, theCustomerId));
        ThenNoEventsOn(EventTagQuery.For(otherCustomer));

        // Arranged history is never the act's, even when the query selects it
        ThenNoEventsOn(EventTagQuery.For(theScreeningId).AndEventsOfType<ScreeningScheduled>());

        var failure = Should.Throw<Exception>(() => ThenEventsOn(EventTagQuery.For(otherCustomer), Specify<SeatReserved>()));
        failure.Message.ShouldContain("MISSING");
    }

    [Fact]
    public async Task a_strong_typed_id_identifies_a_stream_anywhere_a_guid_does()
    {
        // wolverine#4865: theScreeningId, not theScreeningId.Value
        await GivenEvents<SeatAvailability>(theScreeningId, Tagged(new ScreeningScheduled(theScreeningId, 10), theScreeningId));

        await WhenReceived(new ReserveSeat(theScreeningId, theCustomerId, "4C"));

        ThenEventsOn<SeatAvailability>(theScreeningId, Specify<SeatReserved>().With(x => x.Seat, "4C"));
        (await TheAggregate<SeatAvailability>(theScreeningId))!.Seats.ShouldBe(9);
        await ThenAggregate<SeatAvailability>(theScreeningId, Specify<SeatAvailability>().With(x => x.Seats, 9));
    }

    // ---- GH-4940: the consistency boundary the decision loaded ----------------------------------

    private EventTagQuery theSeatBoundary => EventTagQuery.For(theScreeningId).AndEventsOfType<ScreeningScheduled, SeatReserved>();

    [Fact]
    public async Task the_consistency_boundary_is_exactly_the_tag_query_the_decision_loaded()
    {
        await GivenEvents<SeatAvailability>(theScreeningId.Value, Tagged(new ScreeningScheduled(theScreeningId, 10), theScreeningId));

        await WhenReceived(new ReserveSeat(theScreeningId, theCustomerId, "4C"));

        ThenTheConsistencyBoundaryIs(theSeatBoundary);
        ThenTheConsistencyBoundaryIs<SeatAvailability>(EventTagQuery.For(theScreeningId).AndEventsOfType<SeatReserved, ScreeningScheduled>());

        var loaded = LastAct.Boundaries.ShouldHaveSingleItem();
        loaded.ModelType.ShouldBe(typeof(SeatAvailability));
        loaded.Envelope!.Message.ShouldBeOfType<ReserveSeat>();

        // Exact: a boundary wider than the one loaded is as wrong as a narrower one
        var wider = Should.Throw<Exception>(() => ThenTheConsistencyBoundaryIs(EventTagQuery.For(theScreeningId)));
        wider.Message.ShouldContain("MISSING events tagged");
        wider.Message.ShouldContain("UNEXPECTED ScreeningScheduled tagged");

        var narrower = Should.Throw<Exception>(() => ThenTheConsistencyBoundaryIs(EventTagQuery.For(theScreeningId).AndEventsOfType<SeatReserved>()));
        narrower.Message.ShouldContain("UNEXPECTED ScreeningScheduled tagged");

        Should.Throw<Exception>(() => ThenTheConsistencyBoundaryIs<ScreeningScheduled>(theSeatBoundary))
            .Message.ShouldContain("Expected a ScreeningScheduled consistency boundary, but the act loaded only SeatAvailability over");
    }

    [Fact]
    public async Task a_concurrent_event_conflicts_only_when_the_boundary_selects_its_type_and_one_of_its_tags()
    {
        var otherScreening = new ScreeningId(Guid.NewGuid());
        await GivenEvents<SeatAvailability>(theScreeningId.Value, Tagged(new ScreeningScheduled(theScreeningId, 10), theScreeningId));

        await WhenReceived(new ReserveSeat(theScreeningId, theCustomerId, "4C"));

        // Another patron taking a seat at the same screening is inside the boundary
        ThenAConcurrentEventWouldConflict<SeatReserved>(theScreeningId, new CustomerId("CUST-777"));
        ThenAConcurrentEventWouldConflict(Tagged(new SeatReserved(theScreeningId, new CustomerId("CUST-777"), "5D"), theScreeningId));
        ThenAConcurrentEventWouldConflict(Specify<ScreeningScheduled>().Tagged(theScreeningId));

        // A different screening, an event type the decision does not read, or only the customer's tag is not
        ThenAConcurrentEventWouldNotConflict<SeatReserved>(otherScreening, theCustomerId);
        ThenAConcurrentEventWouldNotConflict<ScreeningRenamed>(theScreeningId);
        ThenAConcurrentEventWouldNotConflict(Specify<SeatReserved>().Tagged(theCustomerId));

        Should.Throw<Exception>(() => ThenAConcurrentEventWouldConflict<SeatReserved>(otherScreening))
            .Message.ShouldContain("but it is outside SeatAvailability over");
        Should.Throw<Exception>(() => ThenAConcurrentEventWouldNotConflict<SeatReserved>(theScreeningId))
            .Message.ShouldContain("but it is inside SeatAvailability over");

        // An untagged event is outside every boundary, which would say nothing about the act
        Should.Throw<ArgumentException>(() => ThenAConcurrentEventWouldNotConflict(new SeatReserved(theScreeningId, theCustomerId, "4C")));
    }

    [Fact]
    public async Task a_refused_decision_still_read_its_boundary()
    {
        await GivenEvents<SeatAvailability>(theScreeningId.Value, Tagged(new ScreeningScheduled(theScreeningId, 0), theScreeningId));

        await WhenReceived(new ReserveSeat(theScreeningId, theCustomerId, "4C"));

        ThenNoEvents();
        ThenTheConsistencyBoundaryIs(theSeatBoundary);
        ThenAConcurrentEventWouldConflict<SeatReserved>(theScreeningId);
    }

    [Fact]
    public async Task an_act_that_loads_several_boundaries_has_to_say_which_one_it_means()
    {
        var otherScreening = new ScreeningId(Guid.NewGuid());
        await GivenEvents<SeatAvailability>(theScreeningId.Value, Tagged(new ScreeningScheduled(theScreeningId, 10), theScreeningId));
        await GivenEvents<SeatAvailability>(otherScreening.Value, Tagged(new ScreeningScheduled(otherScreening, 10), otherScreening));

        await WhenTracked("a customer books seats at two screenings", async () =>
        {
            await Host.MessageBus().InvokeAsync(new ReserveSeat(theScreeningId, theCustomerId, "4C"));
            await Host.MessageBus().InvokeAsync(new ReserveSeat(otherScreening, theCustomerId, "4C"));
        });

        LastAct.Boundaries.Count.ShouldBe(2);

        var failure = Should.Throw<Exception>(() => ThenTheConsistencyBoundaryIs(theSeatBoundary));
        failure.Message.ShouldContain("The act loaded 2 consistency boundaries");
        failure.Message.ShouldContain("ThenTheConsistencyBoundaryIs<TModel>");

        // A concurrent event conflicts when any boundary the act loaded selects it
        ThenAConcurrentEventWouldConflict<SeatReserved>(otherScreening);
        ThenAConcurrentEventWouldNotConflict<SeatReserved>(new ScreeningId(Guid.NewGuid()));
    }

    [Fact]
    public async Task the_boundary_steps_read_as_the_specification_says_them()
    {
        await GivenEvents<SeatAvailability>(theScreeningId.Value, Tagged(new ScreeningScheduled(theScreeningId, 10), theScreeningId));

        var recording = await Recordings.RecordAsync(async () =>
        {
            await WhenReceived(new ReserveSeat(theScreeningId, theCustomerId, "4C"));
            ThenTheConsistencyBoundaryIs(theSeatBoundary);
            ThenAConcurrentEventWouldConflict<SeatReserved>(theScreeningId);
            ThenAConcurrentEventWouldNotConflict<ScreeningRenamed>(theScreeningId);
        });

        recording.GatheredFailures().ShouldBeNull();
        var steps = recording.Steps.TakeLast(3).Select(x => x.Text).ToArray();
        steps[0].ShouldBe("the consistency boundary is events tagged theScreeningId (ScreeningScheduled, SeatReserved)");
        steps[1].ShouldBe("a concurrent SeatReserved tagged [theScreeningId] would conflict");
        steps[2].ShouldBe("a concurrent ScreeningRenamed tagged [theScreeningId] would not conflict");

        recording.Steps[^3].Cells.Single(x => x.Name == "boundary").Status.ShouldBe(ResultStatus.success);
    }
}
