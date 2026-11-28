using IntegrationTests;
using JasperFx.CodeGeneration;
using JasperFx.Events;
using JasperFx.Events.EventModeling;
using Marten;
using MartenTests.Dcb.University;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Shouldly;
using Wolverine;
using Wolverine.Configuration.EventModeling;
using Wolverine.Marten;

namespace MartenTests.Dcb;

// GH-6005 (jasperfx#1297): against a Marten-backed host, the derived Event Model reads the tag catalogue from
// the store's registry, the tags each event carries, and what a [DcbModel] command reads -- from the
// registrations and the signatures, never from the Load() body. The host is built and never started, as the
// describe-event-model and event-model commands build it, so no database is touched.
public class event_model_dcb_derived_6005 : IAsyncLifetime
{
    private IHost _host = null!;
    private EventModelDescriptor _model = null!;

    public async ValueTask InitializeAsync()
    {
        _host = Host.CreateDefaultBuilder()
            .UseWolverine(opts =>
            {
                opts.ServiceName = "university";
                opts.Services.AddMarten(m =>
                    {
                        m.Connection(Servers.PostgresConnectionString);
                        m.DatabaseSchemaName = "event_model_dcb_6005";

                        m.Events.RegisterTagType<StudentId>("student").ForAggregate<SubscriptionState>();
                        m.Events.RegisterTagType<CourseId>("course").ForAggregate<SubscriptionState>();
                        m.Events.RegisterTagType<FacultyId>("faculty");

                        m.Projections.LiveStreamAggregation<SubscriptionState>();
                        m.Events.StreamIdentity = StreamIdentity.AsString;
                        m.DisableNpgsqlLogging = true;
                    })
                    .UseLightweightSessions()
                    .IntegrateWithWolverine();

                opts.Discovery.DisableConventionalDiscovery()
                    .IncludeType(typeof(DcbModelSubscribeStudentHandler));
            }).Build();

        // Not started: resolving the code file collections compiles the handler graph, as the commands do
        _ = _host.Services.GetServices<ICodeFileCollection>().ToArray();

        _model = await WolverineEventModelExport.AssembleAsync(_host.Services, token: TestContext.Current.CancellationToken);
    }

    public ValueTask DisposeAsync()
    {
        _host.Dispose();
        return ValueTask.CompletedTask;
    }

    private EventModelSliceDescriptor slice() => _model.Slices.Single(x => x.Name == nameof(DcbModelSubscribeStudentToCourse));

    [Fact]
    public void the_tag_catalogue_is_the_stores_registry()
    {
        _model.Tags.Select(x => x.Name).ShouldBe(["studentId", "courseId", "facultyId"]);
        _model.Tags.ShouldAllBe(x => x.InnerType!.Name == nameof(String));
        _model.Tags.ShouldAllBe(x => x.Provenance == EventModelProvenance.Derived);
    }

    [Fact]
    public void the_command_reads_the_boundary_model_by_the_tags_it_carries()
    {
        slice().Reads.Select(x => (x.Kind, x.Type.Name, x.Tag, x.Member)).ShouldBe(
        [
            (BoundaryReadKind.Model, nameof(SubscriptionState), "studentId", nameof(DcbModelSubscribeStudentToCourse.StudentId)),
            (BoundaryReadKind.Model, nameof(SubscriptionState), "courseId", nameof(DcbModelSubscribeStudentToCourse.CourseId))
        ]);
    }

    [Fact]
    public void the_emitted_event_carries_every_registered_tag_it_has_a_member_of()
    {
        var s = slice();
        s.EmittedEvents.Select(x => x.Name).ShouldBe([nameof(StudentSubscribedToCourse)]);
        s.TagsFor(s.EmittedEvents[0]).ShouldBe(["facultyId", "studentId", "courseId"]);
    }

    [Fact]
    public void the_boundary_query_folds_the_models_applied_events_onto_each_tag()
    {
        var query = _model.BoundaryQueryFor(slice());

        query.Terms.Select(x => x.Tag).ShouldBe(["studentId", "courseId"]);
        // the slice's own append condition...
        query.Terms[0].EventTypes.Select(x => x.Name).ShouldContain(nameof(StudentSubscribedToCourse));
        // ...and what the model folds, read off SubscriptionState's Apply methods
        query.Terms[0].EventTypes.Select(x => x.Name).ShouldContain(nameof(StudentEnrolledInFaculty));
        query.Terms[1].EventTypes.Select(x => x.Name).ShouldContain(nameof(CourseCreated));
    }

    [Fact]
    public void rejection_reasons_are_never_derived()
    {
        // The handler throws five different refusals; none of them is read
        slice().RejectionReasons.ShouldBeEmpty();
    }

    [Fact]
    public void the_fields_of_the_command_and_the_event_carry_their_tags()
    {
        var command = _model.ShapeOf(slice().CommandType!).ShouldNotBeNull();
        command.Find(nameof(DcbModelSubscribeStudentToCourse.StudentId))!.Tag.ShouldBe("studentId");
        command.Find(nameof(DcbModelSubscribeStudentToCourse.CourseId))!.Tag.ShouldBe("courseId");

        var subscribed = _model.ShapeOf(slice().EmittedEvents[0]).ShouldNotBeNull();
        subscribed.Fields.Select(x => x.Tag).ShouldBe(["facultyId", "studentId", "courseId"]);
    }

    [Fact]
    public void describe_event_model_prints_the_boundary_table()
    {
        var text = EventModelReport.Plain(_model).ReplaceLineEndings("\n");

        text.ShouldContain("boundary reads: SubscriptionState (model) by studentId from StudentId; SubscriptionState (model) by courseId from CourseId");
        text.ShouldContain("tags: StudentSubscribedToCourse [facultyId, studentId, courseId]");
        text.ShouldContain("boundary query: studentId: ");
    }
}
