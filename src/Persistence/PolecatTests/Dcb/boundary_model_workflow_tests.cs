using IntegrationTests;
using JasperFx.Events;
using JasperFx.Events.Projections;
using JasperFx.Events.Tags;
using JasperFx.Resources;
using Polecat;
using Polecat.Events;
using Polecat.Projections;
using PolecatTests.Dcb.University;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Shouldly;
using Wolverine;
using Wolverine.Polecat;
using Wolverine.Tracking;

namespace PolecatTests.Dcb;

// Parity port of MartenTests.Dcb.boundary_model_workflow_tests — same DCB scenarios run against
// Polecat (SQL Server) to guarantee the Wolverine + Polecat boundary-model integration covers the
// same cases as the Wolverine + Marten one. Requires the two DCB fixes from JasperFx/polecat#123
// (cross-tag-type EventTagQuery LEFT JOIN; boundary append via StreamAction.Append), shipped in
// Polecat 4.0.0-alpha.10.
public class boundary_model_workflow_tests : IAsyncLifetime
{
    private IHost theHost = null!;
    private IDocumentStore theStore = null!;

    public async Task InitializeAsync()
    {
        theHost = await Host.CreateDefaultBuilder()
            .UseWolverine(opts =>
            {
                opts.Services.AddPolecat(m =>
                    {
                        m.ConnectionString = Servers.SqlServerConnectionString;
                        m.DatabaseSchemaName = "dcb_boundary_tests";

                        // Register tag types for DCB
                        m.Events.RegisterTagType<StudentId>("student")
                            .ForAggregate<SubscriptionState>();
                        m.Events.RegisterTagType<CourseId>("course")
                            .ForAggregate<SubscriptionState>();
                        m.Events.RegisterTagType<FacultyId>("faculty");

                        // FetchForWritingByTags<T> resolves its aggregator via the JasperFx.Events
                        // source generator, which only emits a dispatcher for an aggregate backed by a
                        // single-stream projection. SubscriptionStateProjection is that partial
                        // SingleStreamProjection<SubscriptionState, string> subclass; registered Live it
                        // is computed on demand with no snapshot table. This is Polecat's equivalent of
                        // the Marten DCB test's LiveStreamAggregation<SubscriptionState>(); without it
                        // the boundary fetch throws InvalidProjectionException.
                        m.Projections.Add<SubscriptionStateProjection>(ProjectionLifecycle.Live);

                        m.Events.StreamIdentity = StreamIdentity.AsString;
                    })
                    .UseLightweightSessions()
                    .IntegrateWithWolverine();

                opts.Services.AddResourceSetupOnStartup();
            }).StartAsync();

        theStore = theHost.Services.GetRequiredService<IDocumentStore>();
        await ((DocumentStore)theStore).Database.ApplyAllConfiguredChangesToDatabaseAsync();
        await theStore.Advanced.CleanAllEventDataAsync();
    }

    public async Task DisposeAsync()
    {
        await theHost.StopAsync();
        theHost.Dispose();
    }

    private async Task SeedCourseAndStudent(CourseId courseId, StudentId studentId, int capacity = 10)
    {
        await using var session = theStore.LightweightSession();

        var courseCreated = session.Events.BuildEvent(
            new CourseCreated(FacultyId.Default, courseId, "Math 101", capacity));
        courseCreated.WithTag(courseId);
        var courseStreamKey = courseId.Value;
        session.Events.Append(courseStreamKey, courseCreated);

        var enrolled = session.Events.BuildEvent(
            new StudentEnrolledInFaculty(FacultyId.Default, studentId, "Alice", "Smith"));
        enrolled.WithTag(studentId);
        var studentStreamKey = studentId.Value;
        session.Events.Append(studentStreamKey, enrolled);

        await session.SaveChangesAsync();
    }

    [Fact]
    public async Task can_fetch_for_writing_by_tags_across_multiple_tag_types()
    {
        var courseId = CourseId.Random();
        var studentId = StudentId.Random();

        await SeedCourseAndStudent(courseId, studentId);

        await using var session = theStore.LightweightSession();

        var query = new EventTagQuery()
            .Or<CourseCreated, CourseId>(courseId)
            .Or<StudentEnrolledInFaculty, StudentId>(studentId);

        var boundary = await session.Events.FetchForWritingByTags<SubscriptionState>(query);
        boundary.Events.Count.ShouldBe(2);
        boundary.Aggregate.ShouldNotBeNull();
        boundary.Aggregate.CourseId.ShouldBe(courseId);
        boundary.Aggregate.StudentId.ShouldBe(studentId);
    }

    [Fact]
    public async Task boundary_model_handler_subscribes_student_to_course()
    {
        var courseId = CourseId.Random();
        var studentId = StudentId.Random();

        await SeedCourseAndStudent(courseId, studentId);

        // Invoke the [BoundaryModel] handler
        await theHost.InvokeMessageAndWaitAsync(
            new BoundaryModelSubscribeStudentToCourse(studentId, courseId));

        // Verify the subscription event was appended and discoverable by tag
        await using var session = theStore.LightweightSession();
        var events = await session.Events.QueryByTagsAsync(
            new EventTagQuery().Or<StudentId>(studentId));

        events.ShouldContain(e => e.Data is StudentSubscribedToCourse);
    }

    [Fact]
    public async Task boundary_model_handler_throws_when_student_not_enrolled()
    {
        var courseId = CourseId.Random();
        var studentId = StudentId.Random();

        // Only seed the course, NOT the student
        await using var session = theStore.LightweightSession();
        var courseCreated = session.Events.BuildEvent(
            new CourseCreated(FacultyId.Default, courseId, "Math 101", 10));
        courseCreated.WithTag(courseId);
        session.Events.Append(courseId.Value, courseCreated);
        await session.SaveChangesAsync();

        // The handler should throw because student is not enrolled
        await Should.ThrowAsync<InvalidOperationException>(async () =>
        {
            await theHost.InvokeMessageAndWaitAsync(
                new BoundaryModelSubscribeStudentToCourse(studentId, courseId));
        });
    }

    [Fact]
    public async Task boundary_model_handler_throws_when_course_does_not_exist()
    {
        var courseId = CourseId.Random();
        var studentId = StudentId.Random();

        // Only seed the student, NOT the course
        await using var session = theStore.LightweightSession();
        var enrolled = session.Events.BuildEvent(
            new StudentEnrolledInFaculty(FacultyId.Default, studentId, "Alice", "Smith"));
        enrolled.WithTag(studentId);
        session.Events.Append(studentId.Value, enrolled);
        await session.SaveChangesAsync();

        await Should.ThrowAsync<InvalidOperationException>(async () =>
        {
            await theHost.InvokeMessageAndWaitAsync(
                new BoundaryModelSubscribeStudentToCourse(studentId, courseId));
        });
    }

    [Fact]
    public async Task boundary_model_handler_throws_when_course_is_fully_booked()
    {
        var courseId = CourseId.Random();
        var studentId = StudentId.Random();

        // Create course with capacity = 1 and fill it
        await SeedCourseAndStudent(courseId, studentId, capacity: 1);

        // Subscribe a different student first to fill the course
        var otherStudentId = StudentId.Random();
        await using var session = theStore.LightweightSession();
        var otherEnrolled = session.Events.BuildEvent(
            new StudentEnrolledInFaculty(FacultyId.Default, otherStudentId, "Bob", "Jones"));
        otherEnrolled.WithTag(otherStudentId);
        session.Events.Append(otherStudentId.Value, otherEnrolled);

        var subscribed = session.Events.BuildEvent(
            new StudentSubscribedToCourse(FacultyId.Default, otherStudentId, courseId));
        subscribed.WithTag(otherStudentId, courseId);
        session.Events.Append(otherStudentId.Value, subscribed);
        await session.SaveChangesAsync();

        // Now try to subscribe our student — should fail because course is full
        await Should.ThrowAsync<InvalidOperationException>(async () =>
        {
            await theHost.InvokeMessageAndWaitAsync(
                new BoundaryModelSubscribeStudentToCourse(studentId, courseId));
        });
    }
}
