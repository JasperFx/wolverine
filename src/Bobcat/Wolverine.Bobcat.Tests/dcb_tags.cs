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

    // Givens that seed tagged events are still to be designed (wolverine#4865), so the screening is
    // scheduled straight through Marten here
    public async Task ScheduleAsync(ScreeningId screening, int seats)
    {
        await using var session = Host.Services.GetRequiredService<IDocumentStore>().LightweightSession();
        var scheduled = session.Events.BuildEvent(new ScreeningScheduled(screening, seats));
        scheduled.WithTag(screening);
        session.Events.Append(screening.Value, scheduled);
        await session.SaveChangesAsync();
    }
}

public class dcb_tags(DcbHost app) : WolverineSpec(app.Host), IClassFixture<DcbHost>
{
    private readonly ScreeningId theScreeningId = new(Guid.NewGuid());
    private readonly CustomerId theCustomerId = new("CUST-104");

    [Fact]
    public async Task the_tags_an_event_was_appended_with_are_judged_exactly_in_any_order()
    {
        await app.ScheduleAsync(theScreeningId, 10);

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
        await app.ScheduleAsync(theScreeningId, 10);

        var recording = await Recordings.RecordAsync(async () =>
        {
            await WhenReceived(new ReserveSeat(theScreeningId, theCustomerId, "4C"));
            ThenEvents(Specify<SeatReserved>().With(x => x.Seat, "4C").Tagged(theScreeningId, theCustomerId));
        });

        recording.GatheredFailures().ShouldBeNull();
        var values = recording.Steps.Last().Cells.Single(x => x.Name == "values");
        values.DisplayText.ShouldBe("Seat: 4C, tags: [theScreeningId, CustomerId(CUST-104)]");
    }
}
