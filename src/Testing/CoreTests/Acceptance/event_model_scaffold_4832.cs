using JasperFx.Descriptors;
using JasperFx.Events.EventModeling;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Shouldly;
using Wolverine.Configuration.EventModeling.Scaffolding;
using Microsoft.Extensions.Hosting;
using Wolverine;
using Xunit;

namespace CoreTests.Acceptance.EventModel4832;

// GH-4832: the scaffold command writes implementation skeletons for the slices that are declared but
// have no code behind them yet, from the declared roles and the real stub types, purely through
// ISourceWriter, and in Wolverine's store-agnostic style.
public class event_model_scaffold_4832
{
    private static EventModelDescriptor declared(Action<EventModelBuilder> configure)
    {
        var builder = new EventModelBuilder();
        configure(builder);
        return builder.Build("Clinic").WithProvenance(EventModelProvenance.Declared);
    }

    private static Type? resolve(TypeDescriptor descriptor)
        => typeof(event_model_scaffold_4832).Assembly.GetTypes()
            .FirstOrDefault(x => x.Namespace == typeof(event_model_scaffold_4832).Namespace && x.Name == descriptor.Name);

    private static ScaffoldPlan plan(EventModelDescriptor model, Func<string, bool>? exists = null,
        Func<Type, string?>? findSource = null)
        => SliceScaffolder.Plan(model, new SliceScaffoldOptions
        {
            RootNamespace = "Clinic",
            ResolveType = resolve,
            FileExists = exists ?? (_ => false),
            FindSourceFile = findSource ?? (_ => null)
        });

    [Fact]
    public void a_declared_http_command_becomes_a_store_agnostic_endpoint()
    {
        var result = plan(declared(m => m.InDomain("Scheduling")
            .Slice("ConfirmAppointment").Pattern(SlicePattern.Command).TriggeredBy(TriggerKind.Http)
            .Command<ConfirmAppointmentRequest>().Against<Appointment>().Emits<AppointmentConfirmed>()));

        var file = result.Files.Single(x => x.RelativePath == Path.Combine("Scheduling", "ConfirmAppointment.cs"));

        file.Code.ShouldContain("namespace Clinic.Scheduling;");
        file.Code.ShouldContain("public static class ConfirmAppointmentEndpoint");
        file.Code.ShouldContain("public static ProblemDetails Validate(ConfirmAppointmentRequest confirmAppointmentRequest, Appointment appointment)");
        file.Code.ShouldContain("[Emits(typeof(AppointmentConfirmed))]");
        file.Code.ShouldContain("[WolverinePost(\"/api/confirm-appointment\")]");
        file.Code.ShouldContain("[EmptyResponse]");
        file.Code.ShouldContain("public static EventsToAppend Post(ConfirmAppointmentRequest confirmAppointmentRequest, [WriteModel] Appointment appointment)");
        file.Code.ShouldContain($"using {typeof(Appointment).Namespace};");

        // never a store-specific attribute
        file.Code.ShouldNotContain("WriteAggregate");
        file.Code.ShouldNotContain("Marten");
    }

    [Fact]
    public void an_existing_aggregate_stub_is_never_rewritten_but_the_report_says_what_to_add_and_where()
    {
        var result = plan(
            declared(m => m.Slice("ConfirmAppointment").TriggeredBy(TriggerKind.Http)
                .Command<ConfirmAppointmentRequest>().Against<Appointment>().Emits<AppointmentConfirmed>()),
            findSource: type => $"Domain/{type.Name}.cs");

        result.Files.ShouldNotContain(x => x.RelativePath.Contains("Appointment.cs") && !x.RelativePath.Contains("Confirm"));

        var edit = result.Notices.Single(x => x.Kind == ScaffoldNoticeKind.Edit);
        edit.Path.ShouldBe("Domain/Appointment.cs");
        edit.Subject.ShouldBe($"{typeof(Appointment).FullName} (aggregate)");
        edit.Message.ShouldContain("public void Apply(AppointmentConfirmed e)");
    }

    [Fact]
    public void the_edit_report_finds_the_stubs_source_file_on_disk()
    {
        var finder = ScaffoldCommand.SourceFiles.Finder(AppContext.BaseDirectory);
        var path = finder(typeof(Appointment));

        path.ShouldNotBeNull();
        path.ShouldEndWith("event_model_scaffold_4832.cs");
    }

    [Fact]
    public void a_command_whose_trigger_nothing_reveals_is_reported_unknown_rather_than_guessed()
    {
        var result = plan(declared(m => m.Command<ConfirmAppointmentRequest>()));

        result.Files.ShouldBeEmpty();
        var notice = result.Notices.Single();
        notice.Kind.ShouldBe(ScaffoldNoticeKind.UnknownTrigger);
        notice.Message.ShouldContain("trigger Unknown");
    }

    [Fact]
    public void an_automation_that_starts_a_stream_returns_StartStream_and_scaffolds_the_new_aggregate()
    {
        // Every type here is declared by NAME, so the scaffold writes their stubs too
        var result = plan(declared(m => m.Automation("OpenCase").InDomain("Cases")
            .On("PatientReferred").StartsStream("ClinicCase").Emits("CaseOpened")));

        var handler = result.Files.Single(x => x.RelativePath == Path.Combine("Cases", "OpenCase.cs"));
        handler.Code.ShouldContain("public static class OpenCaseHandler");
        handler.Code.ShouldContain("public static StartStream Handle(PatientReferred patientReferred)");
        handler.Code.ShouldContain("Storage.StartStream<ClinicCase>(");
        handler.Code.ShouldContain("public record PatientReferred;");
        handler.Code.ShouldContain("public record CaseOpened;");
        handler.Code.ShouldNotContain("public record ClinicCase;");

        var aggregate = result.Files.Single(x => x.RelativePath == Path.Combine("Cases", "ClinicCase.cs"));
        aggregate.Code.ShouldContain("public class ClinicCase");
        aggregate.Code.ShouldContain("public void Apply(CaseOpened e)");
    }

    [Fact]
    public void an_automation_issues_its_command_as_an_outgoing_message()
    {
        var result = plan(declared(m => m.Automation("ProposeHomeCheckAppointment")
            .On<HomeCheckAssignmentAccepted>().Command<ProposeAppointment>()));

        var code = result.Files.Single().Code;
        code.ShouldContain("public static OutgoingMessages Handle(HomeCheckAssignmentAccepted homeCheckAssignmentAccepted)");
        code.ShouldContain("new ProposeAppointment(...)");
    }

    [Fact]
    public void a_view_declared_by_name_gets_apply_methods_for_the_events_it_folds()
    {
        var result = plan(declared(m => m.View("AppointmentsQueue").From<AppointmentConfirmed>()));

        var view = result.Files.Single().Code;
        view.ShouldContain("public class AppointmentsQueue");
        view.ShouldContain("public void Apply(AppointmentConfirmed e)");
    }

    [Fact]
    public void a_file_that_already_exists_is_reported_and_left_alone()
    {
        var result = plan(
            declared(m => m.Slice("ConfirmAppointment").TriggeredBy(TriggerKind.MessageHandler)
                .Command<ConfirmAppointmentRequest>()),
            exists: path => path == "ConfirmAppointment.cs");

        result.Files.ShouldBeEmpty();
        result.Notices.Single().Kind.ShouldBe(ScaffoldNoticeKind.Exists);
    }

    [Fact]
    public void a_slice_with_code_behind_it_is_not_declared_only()
    {
        var model = declared(m => m.Slice("ConfirmAppointment").TriggeredBy(TriggerKind.MessageHandler)
            .Command<ConfirmAppointmentRequest>());

        var implemented = EventModelSliceDescriptor.Named("ConfirmAppointment") with
        {
            HandlerType = TypeDescriptor.For(typeof(Appointment))
        };

        var merged = EventModelDescriptor.Merge("Clinic", new[]
        {
            model, new EventModelDescriptor("Clinic", new[] { implemented }).WithProvenance(EventModelProvenance.Derived)
        });

        SliceScaffolder.IsDeclaredOnly(merged.Slices.Single()).ShouldBeFalse();
        plan(merged).Files.ShouldBeEmpty();
    }

    [Fact]
    public void the_scaffolded_handlers_compile()
    {
        var result = plan(declared(m =>
        {
            m.Automation("OpenCase").InDomain("Cases").On("PatientReferred").StartsStream("ClinicCase").Emits("CaseOpened");
            m.Automation("ProposeHomeCheckAppointment").On<HomeCheckAssignmentAccepted>().Command<ProposeAppointment>();
            m.Slice("ConfirmAppointment").TriggeredBy(TriggerKind.MessageHandler).Command<ConfirmAppointmentRequest>()
                .Against<Appointment>().Emits<AppointmentConfirmed>().Publishes<AppointmentReminderScheduled>();
            m.Slice("RecordVisit").TriggeredBy(TriggerKind.MessageHandler).Command("RecordVisitRequest")
                .Reads<Appointment>().Produces("VisitRecord");
            m.View("AppointmentsQueue").From<AppointmentConfirmed>();
        }));

        result.Notices.Where(x => x.Kind != ScaffoldNoticeKind.Wrote && x.Kind != ScaffoldNoticeKind.Edit)
            .ShouldBeEmpty();

        ScaffoldCompilation.Errors(result.Files).ShouldBeEmpty();
    }
}

/// <summary>Compile scaffolded files against everything this test process has loaded.</summary>
public static class ScaffoldCompilation
{
    public static string[] Errors(IEnumerable<ScaffoldFile> files)
    {
        var references = AppDomain.CurrentDomain.GetAssemblies()
            .Where(x => !x.IsDynamic && !string.IsNullOrEmpty(x.Location))
            .Select(x => MetadataReference.CreateFromFile(x.Location));

        var compilation = CSharpCompilation.Create("Scaffolded",
            files.Select(x => CSharpSyntaxTree.ParseText(x.Code, path: x.RelativePath)),
            references,
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));

        return compilation.GetDiagnostics(TestContext.Current.CancellationToken)
            .Where(x => x.Severity == DiagnosticSeverity.Error)
            .Select(x => x.ToString())
            .ToArray();
    }
}

public record ConfirmAppointmentRequest(Guid AppointmentId);

public record AppointmentConfirmed(Guid AppointmentId);

public record AppointmentReminderScheduled(Guid AppointmentId);

public record HomeCheckAssignmentAccepted(Guid HomeCheckId);

public record ProposeAppointment(Guid HomeCheckId);

public class Appointment
{
    public Guid Id { get; set; }
}

// Declared-only is a question only the running application can answer: the declared model and the
// derived chains have to be assembled together first.
public class event_model_scaffold_against_a_host_4832
{
    [Fact]
    public async Task only_the_slices_with_no_code_behind_them_are_scaffolded()
    {
        using var host = await Microsoft.Extensions.Hosting.Host.CreateDefaultBuilder()
            .UseWolverine(opts =>
            {
                opts.ServiceName = "Clinic";
                opts.Discovery.DisableConventionalDiscovery().IncludeType<HomeCheckAssignmentAcceptedHandler>();
                opts.Services.AddEventModel("Clinic", m =>
                {
                    // implemented: the handler below joins it (GH-4831)
                    m.Automation("ProposeHomeCheckAppointment").On<HomeCheckAssignmentAccepted>();

                    // declared only
                    m.Slice("ConfirmAppointment").TriggeredBy(TriggerKind.MessageHandler)
                        .Command<ConfirmAppointmentRequest>();
                });
            })
            .StartAsync(TestContext.Current.CancellationToken);

        var model = (await Wolverine.Configuration.EventModeling.WolverineEventModelExport.AssembleSetAsync(
            host.Services, token: TestContext.Current.CancellationToken)).Models.Single();

        var plan = SliceScaffolder.Plan(model, new SliceScaffoldOptions { RootNamespace = "Clinic" });

        plan.Files.Select(x => x.RelativePath).ShouldBe(new[] { "ConfirmAppointment.cs" });
    }
}

public class HomeCheckAssignmentAcceptedHandler
{
    public ProposeAppointment Handle(HomeCheckAssignmentAccepted e) => new(e.HomeCheckId);
}
