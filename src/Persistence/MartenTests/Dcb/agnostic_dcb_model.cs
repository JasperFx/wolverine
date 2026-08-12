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
using Npgsql;
using Shouldly;
using Wolverine;
using Wolverine.Marten;
using Wolverine.Persistence.EventSourcing;
using Wolverine.Tracking;

namespace MartenTests.Dcb;

// GH-3911: [DcbModel] is the persistence-strategy-agnostic spelling of the Dynamic Consistency
// Boundary workflow, living in Wolverine core. Nothing about it names Marten - it resolves the owning
// store out of the persistence strategies registered on GenerationRules - so this proves it lights up
// against a Marten-backed host, and that Wolverine.Marten's [BoundaryModel] still behaves identically
// now that it is a shell over the same base.
public class agnostic_dcb_model : PostgresqlContext, IAsyncLifetime
{
    private IHost theHost = null!;
    private IDocumentStore theStore = null!;

    public async ValueTask InitializeAsync()
    {
        await using (var conn = new NpgsqlConnection(Servers.PostgresConnectionString))
        {
            await conn.OpenAsync();
            await using var cmd = conn.CreateCommand();
            cmd.CommandText = "DROP SCHEMA IF EXISTS agnostic_dcb_model CASCADE;";
            await cmd.ExecuteNonQueryAsync();
        }

        theHost = await Host.CreateDefaultBuilder()
            .UseWolverine(opts =>
            {
                opts.Services.AddMarten(m =>
                    {
                        m.Connection(Servers.PostgresConnectionString);
                        m.DatabaseSchemaName = "agnostic_dcb_model";

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
                    .IncludeType(typeof(DcbModelSubscribeStudentHandler));
                opts.Durability.Mode = DurabilityMode.Solo;
                opts.Services.AddResourceSetupOnStartup();
            }).StartAsync();

        theStore = theHost.Services.GetRequiredService<IDocumentStore>();
    }

    public async ValueTask DisposeAsync()
    {
        await theHost.StopAsync();
        theHost.Dispose();
    }

    private async Task seedCourseAndStudent(CourseId courseId, StudentId studentId)
    {
        await using var session = theStore.LightweightSession();

        var courseCreated = session.Events.BuildEvent(
            new CourseCreated(FacultyId.Default, courseId, "Math 101", 10));
        courseCreated.WithTag(courseId);
        session.Events.Append(courseId.Value, courseCreated);

        var enrolled = session.Events.BuildEvent(
            new StudentEnrolledInFaculty(FacultyId.Default, studentId, "Alice", "Smith"));
        enrolled.WithTag(studentId);
        session.Events.Append(studentId.Value, enrolled);

        await session.SaveChangesAsync(TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task dcb_model_resolves_marten_and_appends_through_the_boundary()
    {
        var courseId = CourseId.Random();
        var studentId = StudentId.Random();

        await seedCourseAndStudent(courseId, studentId);

        await theHost.InvokeMessageAndWaitAsync(
            new DcbModelSubscribeStudentToCourse(studentId, courseId));

        await using var session = theStore.LightweightSession();
        var events = await session.Events.QueryByTagsAsync(new EventTagQuery().Or<StudentId>(studentId),
            TestContext.Current.CancellationToken);

        events.ShouldContain(e => e.Data is StudentSubscribedToCourse);
    }

    [Fact]
    public async Task dcb_model_applies_transaction_support_the_same_way_boundary_model_does()
    {
        // Chains compile lazily, so drive one message through first
        var courseId = CourseId.Random();
        var studentId = StudentId.Random();
        await seedCourseAndStudent(courseId, studentId);
        await theHost.InvokeMessageAndWaitAsync(new DcbModelSubscribeStudentToCourse(studentId, courseId));

        var chain = theHost.GetRuntime().Handlers.ChainFor<DcbModelSubscribeStudentToCourse>()!;

        chain.IsTransactional.ShouldBeTrue();
    }
}

// The Marten-named attribute is a shell over the core one as of GH-3911. This pins the relationship
// itself, because "still compiles" is most of what keeps existing user code working, and a shell that
// silently stopped deriving would still compile at the *declaration* site.
public class marten_boundary_model_is_a_shell_over_the_core_vocabulary
{
    [Fact]
    public void boundary_model_is_a_dcb_model()
    {
        new BoundaryModelAttribute().ShouldBeAssignableTo<DcbModelAttribute>();
    }
}

public record DcbModelSubscribeStudentToCourse(StudentId StudentId, CourseId CourseId);

#region sample_wolverine_dcb_model_handler

public static class DcbModelSubscribeStudentHandler
{
    public static EventTagQuery Load(DcbModelSubscribeStudentToCourse command)
        => EventTagQuery
            .For(command.CourseId)
            .AndEventsOfType<CourseCreated, CourseCapacityChanged, StudentSubscribedToCourse,
                StudentUnsubscribedFromCourse>()
            .Or(command.StudentId)
            .AndEventsOfType<StudentEnrolledInFaculty, StudentSubscribedToCourse, StudentUnsubscribedFromCourse>();

    public static StudentSubscribedToCourse Handle(
        DcbModelSubscribeStudentToCourse command,
        [DcbModel] SubscriptionState state)
    {
        if (state.StudentId == null)
            throw new InvalidOperationException("Student with given id never enrolled the faculty");

        if (state.CourseId == null)
            throw new InvalidOperationException("Course with given id does not exist");

        return new StudentSubscribedToCourse(FacultyId.Default, command.StudentId, command.CourseId);
    }
}

#endregion
