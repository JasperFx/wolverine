using IntegrationTests;
using JasperFx;
using JasperFx.Events.Aggregation;
using JasperFx.Events.Tags;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Wolverine.Fisher;
using Wolverine.Marten;
using Wolverine.Persistence.EventSourcing;
using Wolverine.Polecat;
using Fisher;
using Marten;
using Polecat;
using Npgsql;
using PolecatStore = global::Polecat.IDocumentStore;

namespace Wolverine.Bobcat.Tests.ConsistencyBoundaries;

// GH-4940: the tag query a DCB decision loaded is recorded by Wolverine's generated code, the same frame
// whichever store serves the boundary, so ThenTheConsistencyBoundaryIs and the "would a concurrent event
// conflict" steps read the same on Marten, Polecat and Fisher. Nothing races: a concurrent event is matched
// against the recorded query.

public record ConferenceId(Guid Value);

public record AttendeeId(string Value);

public record BadgeClaimed(ConferenceId Conference, AttendeeId Attendee);

public record BadgeRevoked(ConferenceId Conference, AttendeeId Attendee);

public record SessionProposed(ConferenceId Conference, string Title);

public record ClaimBadge(ConferenceId Conference, AttendeeId Attendee);

[BoundaryAggregate]
public partial class BadgeLedger
{
    public HashSet<string> Claimed { get; } = [];

    public void Apply(BadgeClaimed e) => Claimed.Add(e.Attendee.Value);
    public void Apply(BadgeRevoked e) => Claimed.Remove(e.Attendee.Value);
}

public static class ClaimBadgeHandler
{
    public static EventTagQuery Load(ClaimBadge command)
        => EventTagQuery.For(command.Conference).AndEventsOfType<BadgeClaimed, BadgeRevoked>();

    // Null until the conference's first badge: nothing inside the boundary yet
    public static BadgeClaimed Handle(ClaimBadge command, [DcbModel] BadgeLedger? ledger)
        => ledger is not null && ledger.Claimed.Contains(command.Attendee.Value)
            ? throw new InvalidOperationException("That attendee already has a badge")
            : new BadgeClaimed(command.Conference, command.Attendee);
}

public abstract class BadgeHost : IAsyncLifetime
{
    public IHost Host { get; private set; } = null!;

    protected abstract void ConfigureStore(WolverineOptions opts);

    protected virtual Task BeforeStartAsync() => Task.CompletedTask;

    protected virtual Task AfterStartAsync() => Task.CompletedTask;

    public async ValueTask InitializeAsync()
    {
        await BeforeStartAsync();

        Host = await Microsoft.Extensions.Hosting.Host.CreateDefaultBuilder()
            .UseWolverine(opts =>
            {
                opts.Durability.Mode = DurabilityMode.Solo;
                opts.Discovery.DisableConventionalDiscovery().IncludeType(typeof(ClaimBadgeHandler));
                ConfigureStore(opts);
            })
            .StartAsync();

        await AfterStartAsync();
    }

    public async ValueTask DisposeAsync()
    {
        if (Host is null) return;

        await Host.StopAsync();
        Host.Dispose();
    }
}

public sealed class MartenBadgeHost : BadgeHost
{
    protected override async Task BeforeStartAsync()
    {
        await using var conn = new NpgsqlConnection(Servers.PostgresConnectionString);
        await conn.OpenAsync();
        await using var drop = conn.CreateCommand();
        drop.CommandText = "DROP SCHEMA IF EXISTS bobcat_consistency_boundaries CASCADE;";
        await drop.ExecuteNonQueryAsync();
    }

    protected override void ConfigureStore(WolverineOptions opts)
        => opts.Services.AddMarten(m =>
            {
                m.Connection(Servers.PostgresConnectionString);
                m.DatabaseSchemaName = "bobcat_consistency_boundaries";
                m.DisableNpgsqlLogging = true;
                m.Events.RegisterTagType<ConferenceId>("conference").ForAggregate<BadgeLedger>();
                m.Events.RegisterTagType<AttendeeId>("attendee");
            })
            .UseLightweightSessions()
            .IntegrateWithWolverine();

    protected override Task AfterStartAsync()
        => Host.Services.GetRequiredService<global::Marten.IDocumentStore>().Storage.ApplyAllConfiguredChangesToDatabaseAsync();
}

public sealed class PolecatBadgeHost : BadgeHost
{
    protected override void ConfigureStore(WolverineOptions opts)
        => opts.Services.AddPolecat(m =>
            {
                m.ConnectionString = Servers.SqlServerConnectionString;
                m.DatabaseSchemaName = "bobcat_consistency_boundaries";
                m.Events.RegisterTagType<ConferenceId>("conference").ForAggregate<BadgeLedger>();
                m.Events.RegisterTagType<AttendeeId>("attendee");
            })
            .UseLightweightSessions()
            .IntegrateWithWolverine();

    protected override Task AfterStartAsync()
        => ((global::Polecat.DocumentStore)Host.Services.GetRequiredService<PolecatStore>()).Database.ApplyAllConfiguredChangesToDatabaseAsync();
}

public sealed class FisherBadgeHost : BadgeHost
{
    private readonly string _file = Path.Combine(Path.GetTempPath(), $"bobcat_consistency_boundaries_{Guid.NewGuid():N}.db");

    protected override void ConfigureStore(WolverineOptions opts)
        => opts.Services.AddFisher(o =>
            {
                o.Connection($"Data Source={_file}");
                o.AutoCreateSchemaObjects = AutoCreate.All;
                o.Events.RegisterTagType<ConferenceId>("conference").ForAggregate<BadgeLedger>();
                o.Events.RegisterTagType<AttendeeId>("attendee");
            })
            .ApplyAllDatabaseChangesOnStartup()
            .IntegrateWithWolverine();
}

public abstract class consistency_boundaries<THost>(THost app) : WolverineSpec(app.Host) where THost : BadgeHost
{
    private readonly ConferenceId theConference = new(Guid.NewGuid());
    private readonly AttendeeId theAttendee = new("ATT-12");

    [Fact]
    public async Task the_decision_is_consistent_over_the_conference_badges_and_nothing_else()
    {
        await WhenReceived(new ClaimBadge(theConference, theAttendee));

        ThenEvents(typeof(BadgeClaimed));

        // Exactly the tag query the handler's Load() returned
        ThenTheConsistencyBoundaryIs(EventTagQuery.For(theConference).AndEventsOfType<BadgeClaimed, BadgeRevoked>());

        // Another badge claimed or revoked for the same conference would have conflicted...
        ThenAConcurrentEventWouldConflict<BadgeClaimed>(theConference, new AttendeeId("ATT-99"));
        ThenAConcurrentEventWouldConflict(Specify<BadgeRevoked>().Tagged(theConference));

        // ...but not one for another conference, nor an event type the decision never read
        ThenAConcurrentEventWouldNotConflict<BadgeClaimed>(new ConferenceId(Guid.NewGuid()), theAttendee);
        ThenAConcurrentEventWouldNotConflict<SessionProposed>(theConference);
    }


    [Fact]
    public async Task the_boundary_is_recorded_whichever_store_loaded_it()
    {
        await WhenReceived(new ClaimBadge(theConference, theAttendee));

        var loaded = LastAct.Boundaries.ShouldHaveSingleItem();
        loaded.ModelType.ShouldBe(typeof(BadgeLedger));
        loaded.Conditions.Count.ShouldBe(2);

        Should.Throw<Exception>(() => ThenTheConsistencyBoundaryIs(EventTagQuery.For(theConference)))
            .Message.ShouldContain("MISSING events tagged");
    }
}

public sealed class marten_consistency_boundaries(MartenBadgeHost app)
    : consistency_boundaries<MartenBadgeHost>(app), IClassFixture<MartenBadgeHost>;

public sealed class polecat_consistency_boundaries(PolecatBadgeHost app)
    : consistency_boundaries<PolecatBadgeHost>(app), IClassFixture<PolecatBadgeHost>;

public sealed class fisher_consistency_boundaries(FisherBadgeHost app)
    : consistency_boundaries<FisherBadgeHost>(app), IClassFixture<FisherBadgeHost>;
