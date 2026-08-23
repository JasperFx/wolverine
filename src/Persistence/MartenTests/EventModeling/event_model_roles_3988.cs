using IntegrationTests;
using JasperFx.Events;
using JasperFx.Events.EventModeling;
using JasperFx.Resources;
using Marten;
using MartenTests.Dcb.University;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Npgsql;
using Shouldly;
using Wolverine;
using Wolverine.Configuration.Capabilities;
using Wolverine.Configuration.EventModeling;
using Wolverine.Marten;
using Wolverine.Tracking;

namespace MartenTests.EventModeling;

// GH-3988: a Marten-backed host with an aggregate handler ([WriteAggregate]), a decider-function
// handler ([AggregateHandler]), a read-aggregate handler and a DCB handler ([BoundaryModel]) reports
// complete Event Modeling roles for all four — with NO source generator anywhere in this test project.
// The roles come off the chains themselves, through the registered IEventModelDefinitionSource and
// the ServiceCapabilities snapshot.
public class event_model_roles_3988 : PostgresqlContext, IAsyncLifetime
{
    private IHost theHost = null!;

    public async ValueTask InitializeAsync()
    {
        await using (var conn = new NpgsqlConnection(Servers.PostgresConnectionString))
        {
            await conn.OpenAsync();
            await using var cmd = conn.CreateCommand();
            cmd.CommandText = "DROP SCHEMA IF EXISTS event_model_roles_3988 CASCADE;";
            await cmd.ExecuteNonQueryAsync();
        }

        theHost = await Host.CreateDefaultBuilder()
            .UseWolverine(opts =>
            {
                opts.ServiceName = "event-model-roles-3988";

                opts.Services.AddMarten(m =>
                    {
                        m.Connection(Servers.PostgresConnectionString);
                        m.DatabaseSchemaName = "event_model_roles_3988";

                        // the DCB half (same setup as agnostic_dcb_model)
                        m.Events.RegisterTagType<StudentId>("student")
                            .ForAggregate<SubscriptionState>();
                        m.Events.RegisterTagType<CourseId>("course")
                            .ForAggregate<SubscriptionState>();
                        m.Events.RegisterTagType<FacultyId>("faculty");
                        m.Projections.LiveStreamAggregation<SubscriptionState>();
                        m.Events.AddEventType<CourseCreated>();
                        m.Events.AddEventType<CourseCapacityChanged>();

                        m.Events.StreamIdentity = StreamIdentity.AsString;
                        m.DisableNpgsqlLogging = true;
                    })
                    .UseLightweightSessions()
                    .IntegrateWithWolverine();

                opts.Discovery.DisableConventionalDiscovery()
                    .IncludeType(typeof(EndTripHandler))
                    .IncludeType(typeof(ConfirmTripHandler))
                    .IncludeType(typeof(TripSummaryHandler))
                    .IncludeType(typeof(WithDcbChangeCourseCapacityHandler));

                opts.Durability.Mode = DurabilityMode.Solo;
                opts.Services.AddResourceSetupOnStartup();
            }).StartAsync();
    }

    public async ValueTask DisposeAsync()
    {
        await theHost.StopAsync();
        theHost.Dispose();
    }

    [Fact]
    public void the_write_aggregate_handler_reports_aggregate_and_emitted_events()
    {
        var model = WolverineEventModelSource.Describe(theHost.GetRuntime());
        var slice = model.Slices.Single(x => x.Name == nameof(EndTrip));

        slice.Pattern.ShouldBe(SlicePattern.Command);
        slice.TriggerKind.ShouldBe(TriggerKind.MessageHandler);
        slice.CommandType!.Name.ShouldBe(nameof(EndTrip));
        slice.HandlerType!.Name.ShouldBe(nameof(EndTripHandler));
        slice.AggregateTypes.Select(x => x.Name).ShouldBe(new[] { nameof(Trip) });
        slice.EmittedEvents.Select(x => x.Name).ShouldBe(new[] { nameof(TripEnded) });
        slice.PublishedMessages.ShouldBeEmpty();
    }

    [Fact]
    public void the_decider_function_handler_infers_its_aggregate()
    {
        var model = WolverineEventModelSource.Describe(theHost.GetRuntime());
        var slice = model.Slices.Single(x => x.Name == nameof(ConfirmTrip));

        slice.AggregateTypes.Select(x => x.Name).ShouldBe(new[] { nameof(Trip) });
        slice.EmittedEvents.Select(x => x.Name).ShouldBe(new[] { nameof(TripConfirmed) });
    }

    [Fact]
    public void the_read_aggregate_handler_reads_the_aggregate_as_a_read_model()
    {
        var model = WolverineEventModelSource.Describe(theHost.GetRuntime());
        var slice = model.Slices.Single(x => x.Name == nameof(GetTripSummary));

        slice.AggregateTypes.ShouldBeEmpty();
        slice.ReadModelTypes.Select(x => x.Name).ShouldBe(new[] { nameof(Trip) });
        slice.EmittedEvents.ShouldBeEmpty();
        slice.PublishedMessages.Select(x => x.Name).ShouldBe(new[] { nameof(TripSummary) });
    }

    [Fact]
    public void the_dcb_handler_reports_the_boundary_model_and_its_event()
    {
        var model = WolverineEventModelSource.Describe(theHost.GetRuntime());
        var slice = model.Slices.Single(x => x.Name == nameof(ChangeCourseCapacity));

        slice.AggregateTypes.Select(x => x.Name).ShouldBe(new[] { nameof(WithDcbChangeCourseCapacityHandler.State) });
        slice.EmittedEvents.Select(x => x.Name).ShouldBe(new[] { nameof(CourseCapacityChanged) });

        model.Aggregates.Single(x => x.Type.Name == nameof(WithDcbChangeCourseCapacityHandler.State)).Kind
            .ShouldBe(AggregateKind.BoundaryModel);
    }

    [Fact]
    public void the_aggregate_elements_carry_kind_and_applied_events()
    {
        var model = WolverineEventModelSource.Describe(theHost.GetRuntime());

        var trip = model.Aggregates.Single(x => x.Type.Name == nameof(Trip));
        trip.Kind.ShouldBe(AggregateKind.WriteAggregate);
        trip.AppliedEvents.Select(x => x.Name).ShouldBe(new[] { nameof(TripStarted), nameof(TripEnded), nameof(TripConfirmed) });
    }

    [Fact]
    public async Task the_roles_reach_the_capabilities_snapshot_and_the_discovery_seam()
    {
        var capabilities = await ServiceCapabilities.ReadFrom(theHost.GetRuntime(), null, CancellationToken.None);

        capabilities.EventModel.ShouldNotBeNull();
        var fromCapabilities = capabilities.EventModel.Slices.Single(x => x.Name == nameof(EndTrip));
        fromCapabilities.AggregateTypes.Select(x => x.Name).ShouldBe(new[] { nameof(Trip) });
        fromCapabilities.EmittedEvents.Select(x => x.Name).ShouldBe(new[] { nameof(TripEnded) });

        var perHandler = capabilities.Messages.Single(x => x.Type.Name == nameof(EndTrip)).Handlers.Single().EventModel!;
        perHandler.AggregateTypes.Select(x => x.Name).ShouldBe(new[] { nameof(Trip) });

        var discovered = await EventModelDiscovery.AssembleAsync(theHost.Services, TestContext.Current.CancellationToken);
        var viaSeam = discovered.Single(x => x.Name == "event-model-roles-3988").Slices.Single(x => x.Name == nameof(EndTrip));
        viaSeam.EmittedEvents.Select(x => x.Name).ShouldBe(new[] { nameof(TripEnded) });
    }

    [Fact]
    public async Task the_roles_survive_the_aggregate_workflow_actually_running()
    {
        // Codegen applies the aggregate handler workflow, which records AggregateHandling on the chain's
        // tags — the same roles, now also from the tag path. Drive one message through to prove the two
        // agree once the chain has been assembled.
        var tripId = $"trip-{Guid.NewGuid():N}";
        var store = theHost.Services.GetRequiredService<IDocumentStore>();
        await using (var session = store.LightweightSession())
        {
            session.Events.StartStream<Trip>(tripId, new TripStarted());
            await session.SaveChangesAsync(TestContext.Current.CancellationToken);
        }

        await theHost.InvokeMessageAndWaitAsync(new EndTrip(tripId));

        var slice = WolverineEventModelSource.Describe(theHost.GetRuntime()).Slices.Single(x => x.Name == nameof(EndTrip));
        slice.AggregateTypes.Select(x => x.Name).ShouldBe(new[] { nameof(Trip) });
        slice.EmittedEvents.Select(x => x.Name).ShouldBe(new[] { nameof(TripEnded) });
    }
}

public record StartTrip(string TripId);
public record EndTrip(string TripId);
public record ConfirmTrip(string TripId);
public record GetTripSummary(string TripId);
public record TripStarted;
public record TripEnded;
public record TripConfirmed;
public record TripSummary(string TripId, bool Ended);

public class Trip
{
    public string Id { get; set; } = null!;
    public bool Ended { get; set; }
    public bool Confirmed { get; set; }

    public void Apply(TripStarted started) { }
    public void Apply(TripEnded ended) => Ended = true;
    public void Apply(TripConfirmed confirmed) => Confirmed = true;
}

public static class EndTripHandler
{
    public static TripEnded Handle(EndTrip command, [WriteAggregate] Trip trip) => new();
}

[AggregateHandler]
public static class ConfirmTripHandler
{
    public static TripConfirmed Handle(ConfirmTrip command, Trip trip) => new();
}

public static class TripSummaryHandler
{
    public static TripSummary Handle(GetTripSummary query, [ReadAggregate] Trip trip) => new(trip.Id, trip.Ended);
}
