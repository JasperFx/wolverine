using JasperFx.Events.EventModeling;
using Spectre.Console;
using Wolverine.Configuration.EventModeling;
using Xunit;

namespace CoreTests.Acceptance.EventModel4917;

// GH-4917: the assembled Event Model as a person reads it
public class describe_event_model_4917
{
    private static EventModelDescriptor model()
    {
        var builder = new EventModelBuilder();
        builder.InDomain("Shelter");
        builder.InChapter("BookingAppointments");
        builder.ForAggregate("Appointment");
        builder.Command("ConfirmAppointment").TriggeredBy("Confirm Appointment", TriggerKind.Human)
            .Emits("AppointmentConfirmed").LinksToSpecification("ConfirmAppointment/appointment confirmed");
        builder.Command("ProposeAppointment").NoAggregate().Emits("AppointmentProposed")
            .Hotspot("who proposes a slot when no staff are free?");
        builder.InChapter("Volunteering");
        builder.View("VolunteerApplicationsQueue").From("VolunteerApplicationSubmitted");

        return builder.Build("CritterCrush").WithProvenance(EventModelProvenance.Declared);
    }

    [Fact]
    public void plain_text_groups_by_domain_then_chapter_and_says_each_slices_roles()
    {
        var text = EventModelReport.Plain(model()).ReplaceLineEndings("\n");

        text.ShouldContain("Event Model 'CritterCrush': 3 slice(s)");
        text.ShouldContain("""
            Shelter
              BookingAppointments
                ConfirmAppointment  [Command]  <- Human "Confirm Appointment"  (declared)
                  command: ConfirmAppointment
                  aggregate: Appointment (the chapter's ForAggregate default)
                  emits: AppointmentConfirmed
                  handled by: nothing yet
                  spec: ConfirmAppointment/appointment confirmed
            """.ReplaceLineEndings("\n"));
        text.ShouldContain("aggregate: none, declared with NoAggregate()");
        text.ShouldContain("! Prose: who proposes a slot when no staff are free?");
        text.ShouldContain("  Volunteering\n    VolunteerApplicationsQueue  [View]");
    }

    [Fact]
    public void plain_text_has_no_tree_glyphs_and_ends_with_a_summary()
    {
        var text = EventModelReport.Plain(model());

        text.ShouldNotContain("├");
        text.ShouldNotContain("└");
        text.ShouldContain("BookingAppointments | 2 | 0 | 1 | 1");
        text.ShouldContain("Volunteering | 1 | 0 | 0 | 0");
    }

    [Fact]
    public void filters_narrow_to_a_chapter_or_to_slices_with_hotspots()
    {
        EventModelReport.Slices(model(), new EventModelReportFilter(Chapter: "Volunteering"))
            .Select(x => x.Heading.Split(' ')[0]).ShouldBe(["VolunteerApplicationsQueue"]);

        EventModelReport.Slices(model(), new EventModelReportFilter(HotspotsOnly: true))
            .Select(x => x.Heading.Split(' ')[0]).ShouldBe(["ProposeAppointment"]);
    }

    [Fact]
    public void the_terminal_tree_renders_the_same_slices()
    {
        var writer = new StringWriter();
        var console = AnsiConsole.Create(new AnsiConsoleSettings
        {
            Ansi = AnsiSupport.No, ColorSystem = ColorSystemSupport.NoColors, Out = new AnsiConsoleOutput(writer)
        });
        console.Profile.Width = 200;

        EventModelReport.Write(console, model());

        var rendered = writer.ToString();
        rendered.ShouldContain("ConfirmAppointment  [Command]");
        rendered.ShouldContain("who proposes a slot when no staff are free?");
        rendered.ShouldContain("Implemented");
    }
}
