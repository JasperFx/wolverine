using JasperFx.Descriptors;
using JasperFx.Events.EventModeling;
using Shouldly;
using Wolverine.Configuration.EventModeling.Scaffolding;
using Xunit;

namespace CoreTests.Acceptance.EventModel4832;

// GH-6006 (jasperfx#1297): a command slice that declares Refuses("...") gets one guard per reason in the
// handler skeleton, the message in place -- a ProblemDetails for an HTTP endpoint, a thrown exception for a
// message handler -- so the generated spec's ThenRefusedWith("...") binds on day one. The rule behind each
// guard is the one thing the model cannot say, and stays a TODO.
public class event_model_scaffold_refusals_6006
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

    private static ScaffoldPlan plan(EventModelDescriptor model, Func<string, bool>? exists = null)
        => SliceScaffolder.Plan(model, new SliceScaffoldOptions
        {
            RootNamespace = "Clinic",
            ResolveType = resolve,
            FileExists = exists ?? (_ => false),
            FindSourceFile = _ => null
        });

    [Fact]
    public void an_http_command_refuses_each_declared_reason_with_a_problem_details_in_validate()
    {
        var result = plan(declared(m => m.Slice("ConfirmAppointment").TriggeredBy(TriggerKind.Http)
            .Command<ConfirmAppointmentRequest>().Against<Appointment>().Emits<AppointmentConfirmed>()
            .Refuses("Appointment is already confirmed")
            .Refuses("The slot has been taken")));

        var code = result.Files.Single().Code;

        code.ShouldContain("public static ProblemDetails Validate(ConfirmAppointmentRequest command, Appointment appointment)");
        code.ShouldContain("var appointmentIsAlreadyConfirmed = false;   // TODO: when is \"Appointment is already confirmed\" true?");
        code.ShouldContain("if (appointmentIsAlreadyConfirmed) return new ProblemDetails { Detail = \"Appointment is already confirmed\", Status = 400 };");
        code.ShouldContain("if (theSlotHasBeenTaken) return new ProblemDetails { Detail = \"The slot has been taken\", Status = 400 };");
        code.ShouldContain("return WolverineContinue.NoProblems;");
        code.ShouldNotContain("TODO: the guards your specifications describe");

        // the guards live in Validate, not in the endpoint method
        code.IndexOf("if (appointmentIsAlreadyConfirmed)", StringComparison.Ordinal)
            .ShouldBeLessThan(code.IndexOf("[WolverinePost(", StringComparison.Ordinal));

        var notice = result.Notices.Single(x => x.Code == ScaffoldNoticeCodes.RefusalGuards);
        notice.Kind.ShouldBe(ScaffoldNoticeKind.Wrote);
        notice.Subject.ShouldBe("ConfirmAppointment");
        notice.Message.ShouldContain("2 guard(s) written for the declared refusals \"Appointment is already confirmed\", \"The slot has been taken\"");
        notice.Message.ShouldContain("as a ProblemDetails");
        notice.Path.ShouldBe(result.Files.Single().RelativePath);
    }

    [Fact]
    public void a_message_handler_refuses_each_declared_reason_by_throwing_with_the_message()
    {
        var result = plan(declared(m => m.Slice("ConfirmAppointment").TriggeredBy(TriggerKind.MessageHandler)
            .Command<ConfirmAppointmentRequest>().Against<Appointment>().Emits<AppointmentConfirmed>()
            .Refuses("Appointment is already confirmed")));

        var code = result.Files.Single().Code;

        code.ShouldContain("public static AppointmentConfirmed? Handle(ConfirmAppointmentRequest command, [WriteModel] Appointment appointment)");
        code.ShouldContain("var appointmentIsAlreadyConfirmed = false;   // TODO: when is \"Appointment is already confirmed\" true?");
        code.ShouldContain("if (appointmentIsAlreadyConfirmed) throw new InvalidOperationException(\"Appointment is already confirmed\");");
        code.ShouldNotContain("ProblemDetails");

        // the guard comes before the shape of the decision, which is what the throw at the end is for
        code.IndexOf("if (appointmentIsAlreadyConfirmed)", StringComparison.Ordinal)
            .ShouldBeLessThan(code.IndexOf("Fill this in and delete the throw", StringComparison.Ordinal));

        result.Notices.Single(x => x.Code == ScaffoldNoticeCodes.RefusalGuards).Message.ShouldContain("by throwing");

        ScaffoldCompilation.Errors(result.Files).ShouldBeEmpty();
    }

    [Fact]
    public void reasons_that_collide_or_start_oddly_still_get_distinct_legal_identifiers()
    {
        var code = plan(declared(m => m.Slice("ConfirmAppointment").TriggeredBy(TriggerKind.MessageHandler)
            .Command<ConfirmAppointmentRequest>().Against<Appointment>().Emits<AppointmentConfirmed>()
            .Refuses("Course is full")
            .Refuses("Course is full!")
            .Refuses("3 strikes")
            .Refuses("Says \"no\""))).Files.Single().Code;

        code.ShouldContain("var courseIsFull = false;");
        code.ShouldContain("var courseIsFull2 = false;");
        code.ShouldContain("var refusal3Strikes = false;");
        code.ShouldContain("throw new InvalidOperationException(\"Says \\\"no\\\"\");");
    }

    [Fact]
    public void a_slice_without_refusals_keeps_the_todo_and_gets_no_guard_notice()
    {
        var result = plan(declared(m => m.Slice("ConfirmAppointment").TriggeredBy(TriggerKind.Http)
            .Command<ConfirmAppointmentRequest>().Against<Appointment>().Emits<AppointmentConfirmed>()));

        result.Files.Single().Code.ShouldContain("// TODO: the guards your specifications describe.");
        result.Notices.ShouldNotContain(x => x.Code == ScaffoldNoticeCodes.RefusalGuards);
    }

    [Fact]
    public void an_existing_handler_is_never_touched_for_its_refusals()
    {
        var result = plan(declared(m => m.Slice("ConfirmAppointment").TriggeredBy(TriggerKind.Http)
            .Command<ConfirmAppointmentRequest>().Against<Appointment>().Emits<AppointmentConfirmed>()
            .Refuses("Appointment is already confirmed")), exists: _ => true);

        result.Files.ShouldBeEmpty();
        result.Notices.ShouldContain(x => x.Code == ScaffoldNoticeCodes.FileExists);
        result.Notices.ShouldNotContain(x => x.Code == ScaffoldNoticeCodes.RefusalGuards);
    }

    [Fact]
    public void an_automation_reacting_to_an_event_gets_no_guards_for_the_command_it_issues()
    {
        // The refusal belongs to the command slice; an automation issues the command, it does not decide it
        var result = plan(declared(m => m.Slice("RemindOfAppointment").Pattern(SlicePattern.Automation)
            .On<AppointmentConfirmed>().Command<ConfirmAppointmentRequest>()
            .Refuses("Appointment is already confirmed")));

        result.Files.Single().Code.ShouldNotContain("appointmentIsAlreadyConfirmed");
        result.Notices.ShouldNotContain(x => x.Code == ScaffoldNoticeCodes.RefusalGuards);
    }
}
