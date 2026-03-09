using IntegrationTests;
using JasperFx.Events;
using JasperFx.Events.Tags;
using JasperFx.Resources;
using Marten;
using Marten.Events;
using MartenTests.Dcb.University;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Npgsql;
using Shouldly;
using Wolverine;
using Wolverine.Marten;
using Wolverine.Tracking;

namespace MartenTests.Dcb;

public class boundary_model_workflow_tests : PostgresqlContext, IAsyncLifetime
{
    private IHost theHost;
    private IDocumentStore theStore;

    public async Task InitializeAsync()
    {
        // Drop the schema if it exists to avoid migration conflicts
        await using (var conn = new NpgsqlConnection(Servers.PostgresConnectionString))
        {
            await conn.OpenAsync();
            await using var cmd = conn.CreateCommand();
            cmd.CommandText = "DROP SCHEMA IF EXISTS dcb_boundary_tests CASCADE;";
            await cmd.ExecuteNonQueryAsync();
        }

        theHost = await Host.CreateDefaultBuilder()
            .UseWolverine(opts =>
            {
                opts.Services.AddMarten(m =>
                    {
                        m.Connection(Servers.PostgresConnectionString);
                        m.DatabaseSchemaName = "dcb_boundary_tests";

                        // Register tag types for DCB
                        m.Events.RegisterTagType<StudentId>("student")
                            .ForAggregate<SubscriptionState>();
                        m.Events.RegisterTagType<CourseId>("course")
                            .ForAggregate<SubscriptionState>();
                        m.Events.RegisterTagType<FacultyId>("faculty");

                        // Register event types
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

                opts.Services.AddResourceSetupOnStartup();
            }).StartAsync();

        theStore = theHost.Services.GetRequiredService<IDocumentStore>();
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
