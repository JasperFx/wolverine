using IntegrationTests;
using JasperFx.Events;
using JasperFx.Events.Tags;
using JasperFx.Resources;
using Marten;
using Marten.Events;
using MartenTests.AncillaryStores;
using MartenTests.Dcb.University;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Npgsql;
using Shouldly;
using Wolverine;
using Wolverine.Marten;
using Wolverine.Tracking;

namespace MartenTests.Dcb;

// Regression: two [BoundaryModel] parameters on the same chain (Validate +
// Handle) used to emit duplicate var declarations -> CS0128.
public record TwoBoundaryModelParamsCommand(StudentId StudentId, CourseId CourseId);

public static class TwoBoundaryModelParamsHandler
{
    public static EventTagQuery Load(TwoBoundaryModelParamsCommand command)
        => EventTagQuery
            .For(command.CourseId)
            .AndEventsOfType<CourseCreated, CourseCapacityChanged, StudentSubscribedToCourse, StudentUnsubscribedFromCourse>()
            .Or(command.StudentId)
            .AndEventsOfType<StudentEnrolledInFaculty, StudentSubscribedToCourse, StudentUnsubscribedFromCourse>();

    public static HandlerContinuation Validate(
        TwoBoundaryModelParamsCommand command,
        [BoundaryModel] SubscriptionState state,
        ILogger logger)
    {
        if (state.StudentId == null)
        {
            logger.LogDebug("Student {StudentId} not enrolled", command.StudentId);
            return HandlerContinuation.Stop;
        }

        return HandlerContinuation.Continue;
    }

    public static StudentSubscribedToCourse Handle(
        TwoBoundaryModelParamsCommand command,
        [BoundaryModel] SubscriptionState state)
    {
        return new StudentSubscribedToCourse(FacultyId.Default, command.StudentId, command.CourseId);
    }
}

public class dedup_load_boundary_frame_tests : PostgresqlContext, IAsyncLifetime
{
    private IHost theHost = null!;
    private IDocumentStore theStore = null!;

    public async Task InitializeAsync()
    {
        await using (var conn = new NpgsqlConnection(Servers.PostgresConnectionString))
        {
            await conn.OpenAsync();
            await using var cmd = conn.CreateCommand();
            cmd.CommandText = "DROP SCHEMA IF EXISTS dcb_dedup_tests CASCADE;";
            await cmd.ExecuteNonQueryAsync();
        }

        theHost = await Host.CreateDefaultBuilder()
            .UseWolverine(opts =>
            {
                opts.Services.AddMarten(m =>
                    {
                        m.Connection(Servers.PostgresConnectionString);
                        m.DatabaseSchemaName = "dcb_dedup_tests";

                        m.Events.RegisterTagType<StudentId>("student")
                            .ForAggregate<SubscriptionState>();
                        m.Events.RegisterTagType<CourseId>("course")
                            .ForAggregate<SubscriptionState>();
                        m.Events.RegisterTagType<FacultyId>("faculty");

                        m.Projections.LiveStreamAggregation<SubscriptionState>();

                        m.Events.AddEventType<CourseCreated>();
                        m.Events.AddEventType<CourseCapacityChanged>();
                        m.Events.AddEventType<StudentEnrolledInFaculty>();
                        m.Events.AddEventType<StudentSubscribedToCourse>();
                        m.Events.AddEventType<StudentUnsubscribedFromCourse>();

                        m.Events.StreamIdentity = StreamIdentity.AsString;

                        m.DisableNpgsqlLogging = true;
                    })
                    .UseLightweightSessions()
                    .IntegrateWithWolverine();

                opts.Discovery.DisableConventionalDiscovery()
                    .IncludeType(typeof(TwoBoundaryModelParamsHandler));
                opts.Durability.Mode = DurabilityMode.Solo;
                opts.Services.AddResourceSetupOnStartup();
            }).StartAsync();

        theStore = theHost.Services.GetRequiredService<IDocumentStore>();
    }

    public async Task DisposeAsync()
    {
        await theHost.StopAsync();
        theHost.Dispose();
    }

    [Fact]
    public async Task chain_with_two_boundary_model_parameters_compiles_and_runs()
    {
        var courseId = CourseId.Random();
        var studentId = StudentId.Random();

        await using (var session = theStore.LightweightSession())
        {
            var courseCreated = session.Events.BuildEvent(
                new CourseCreated(FacultyId.Default, courseId, "Math 101", 10));
            courseCreated.WithTag(courseId);
            session.Events.Append(courseId.Value, courseCreated);

            var enrolled = session.Events.BuildEvent(
                new StudentEnrolledInFaculty(FacultyId.Default, studentId, "Alice", "Smith"));
            enrolled.WithTag(studentId);
            session.Events.Append(studentId.Value, enrolled);

            await session.SaveChangesAsync();
        }

        // Pre-fix: this throws at handler-compilation with CS0128.
        await theHost.InvokeMessageAndWaitAsync(
            new TwoBoundaryModelParamsCommand(studentId, courseId));

        await using var verifySession = theStore.LightweightSession();
        var events = await verifySession.Events.QueryByTagsAsync(
            new EventTagQuery().Or<StudentId>(studentId));

        events.ShouldContain(e => e.Data is StudentSubscribedToCourse);
    }
}
