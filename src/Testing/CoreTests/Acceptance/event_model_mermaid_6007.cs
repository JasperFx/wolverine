using JasperFx.CommandLine;
using JasperFx.Events.EventModeling;
using Microsoft.Extensions.Hosting;
using Wolverine;
using Wolverine.Configuration.EventModeling;
using Xunit;

namespace CoreTests.Acceptance.EventModel6007;

// GH-6007: the assembled Event Model as a Mermaid `eventmodeling` diagram (Mermaid 11.15+), from
// `event-model --format mermaid` and `describe-event-model --mermaid`
public class event_model_mermaid_6007
{
    // One slice of every pattern, with the things the diagram has to carry: a human trigger with a label,
    // several events on one command, a view over another slice's event, an automation reading a read model,
    // a translation from an external system, a query, bound specifications, and a name that is not an
    // identifier (the HTTP route)
    private static EventModelDescriptor model()
    {
        var builder = new EventModelBuilder();
        builder.InDomain("Shelter");
        builder.InChapter("BookingAppointments");
        builder.ForAggregate("Appointment");

        builder.Command("ProposeAppointment").TriggeredBy("Staff proposes a slot", TriggerKind.Human)
            .Emits("AppointmentProposed").Emits("SlotHeld")
            .LinksToSpecification("Proposing an appointment/holds the slot")
            .Hotspot("who proposes when nobody is free?");

        builder.Command("ConfirmAppointment").TriggeredBy(TriggerKind.Http)
            .Emits("AppointmentConfirmed");

        builder.View("AppointmentsCalendar").From("AppointmentProposed").From("AppointmentConfirmed")
            .Produces("AppointmentsCalendar")
            .LinksToSpecification("The calendar/shows proposed and confirmed appointments");

        builder.Automation("RemindOfAppointment").On("AppointmentConfirmed").Reads("AppointmentsCalendar")
            .HandledBy("AppointmentReminder").Command("SendReminder").Emits("ReminderSent");

        builder.InChapter("Partners");
        builder.Translation("ImportPartnerBookings").NoAggregate().On("PartnerBookingReceived")
            .ExternalSystem("PartnerPortal", ExternalSystemDirection.Inbound)
            .HandledBy("PartnerBookingImporter").Emits("AppointmentProposed");

        builder.Query("GET /appointments/{id}").NoAggregate().TriggeredBy("Staff opens an appointment", TriggerKind.Human)
            .Reads("AppointmentsCalendar");

        return builder.Build("CritterCrush").WithProvenance(EventModelProvenance.Declared);
    }

    [Fact]
    public void renders_the_declared_model_as_an_eventmodeling_diagram()
    {
        var text = EventModelMermaid.Render(model()).ReplaceLineEndings("\n");

        text.ShouldBe("""
            ---
            title: "CritterCrush"
            ---
            eventmodeling

                %% ProposeAppointment (Command) in Shelter / BookingAppointments
                rf 01 ui Appointment.StaffProposesASlot
                tf 02 cmd Appointment.ProposeAppointment
                tf 03 evt Appointment.AppointmentProposed
                rf 04 evt Appointment.SlotHeld ->> 02

                %% ConfirmAppointment (Command) in Shelter / BookingAppointments
                rf 05 cmd Appointment.ConfirmAppointment
                tf 06 evt Appointment.AppointmentConfirmed

                %% ImportPartnerBookings (Translation) in Shelter / Partners
                rf 07 evt PartnerPortal.PartnerBookingReceived
                tf 08 pcr Shelter.PartnerBookingImporter
                tf 09 evt Appointment.AppointmentProposed

                %% RemindOfAppointment (Automation) in Shelter / BookingAppointments
                rf 10 rmo Shelter.AppointmentsCalendar ->> 06
                tf 11 pcr Shelter.AppointmentReminder
                tf 12 cmd Shelter.SendReminder
                tf 13 evt Shelter.ReminderSent

                %% AppointmentsCalendar (View) in Shelter / BookingAppointments
                rf 14 rmo Shelter.AppointmentsCalendar ->> 03 ->> 06 ->> 09

                %% GET /appointments/{id} (Query) in Shelter / Partners
                rf 15 ui Shelter.StaffOpensAnAppointment
                tf 16 rmo Shelter.AppointmentsCalendar

                entity AppointmentConfirmed
                entity AppointmentProposed
                entity AppointmentsCalendar
                entity ProposeAppointment
                entity SlotHeld

                %% spec: Proposing an appointment/holds the slot
                gwt 02 given evt AppointmentProposed evt SlotHeld when cmd ProposeAppointment then evt AppointmentProposed evt SlotHeld

                %% spec: The calendar/shows proposed and confirmed appointments
                gwt 14 given evt AppointmentProposed evt AppointmentConfirmed then rmo AppointmentsCalendar

            """.ReplaceLineEndings("\n"));
    }

    [Theory]
    [InlineData("GET /appointments/{id}", "GETAppointmentsId")]
    [InlineData("New customer walks in", "NewCustomerWalksIn")]
    [InlineData("the fraud desk", "TheFraudDesk")]
    [InlineData("IEnumerable<Account>", "IEnumerableAccount")]
    [InlineData("already_an_identifier", "Already_an_identifier")]
    [InlineData("3rd party", "_3rdParty")]
    [InlineData("---", "_")]
    [InlineData("", "_")]
    [InlineData(null, "_")]
    public void identifiers_are_reduced_to_the_grammar_deterministically(string? name, string expected)
    {
        EventModelMermaid.Identifier(name).ShouldBe(expected);
    }

    [Fact]
    public void a_slice_with_no_pattern_is_drawn_as_a_command()
    {
        var builder = new EventModelBuilder();
        builder.Slice("Something").Command("DoSomething").Emits("SomethingDone");
        var text = EventModelMermaid.Render(builder.Build("Plain")).ReplaceLineEndings("\n");

        text.ShouldContain("%% Something (no pattern)\n    rf 01 cmd DoSomething\n    tf 02 evt SomethingDone\n");
    }

    [Fact]
    public void a_view_over_events_nothing_emits_draws_the_events_itself()
    {
        var builder = new EventModelBuilder();
        builder.View("Ledger").From("MoneyMoved").Produces("Ledger");
        var text = EventModelMermaid.Render(builder.Build("Plain")).ReplaceLineEndings("\n");

        text.ShouldContain("rf 01 evt MoneyMoved\n    tf 02 rmo Ledger\n");
    }

    [Fact]
    public void a_specification_on_a_slice_the_model_cannot_state_a_given_for_is_only_a_comment()
    {
        var builder = new EventModelBuilder();
        builder.Command("Ping").LinksToSpecification("Ping/pongs");
        var text = EventModelMermaid.Render(builder.Build("Plain")).ReplaceLineEndings("\n");

        text.ShouldContain("%% spec: Ping/pongs (no given / then the model can state)");
        text.ShouldNotContain("gwt");
    }

    // The commands

    private static IHostBuilder hostBuilder() => Host.CreateDefaultBuilder()
        .UseWolverine(opts =>
        {
            opts.ServiceName = "mermaid-6007";
            opts.Discovery.DisableConventionalDiscovery().IncludeType(typeof(RenameThingHandler));
        });

    private static async Task<(bool Succeeded, string Output)> runAsync<TInput>(JasperFxAsyncCommand<TInput> command, TInput input)
    {
        var output = new StringWriter();
        var original = Console.Out;
        Console.SetOut(output);
        try
        {
            var succeeded = await command.Execute(input);
            return (succeeded, output.ToString());
        }
        finally
        {
            Console.SetOut(original);
        }
    }

    [Fact]
    public async Task event_model_format_mermaid_writes_the_diagram_to_event_model_mmd_by_default()
    {
        var directory = Path.Combine(Path.GetTempPath(), $"mermaid-6007-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, EventModelInput.DefaultMermaidFile);

        var input = new EventModelInput { HostBuilder = hostBuilder(), FormatFlag = "mermaid", OutputFlag = path };
        var (succeeded, output) = await runAsync(new EventModelCommand(), input);

        try
        {
            succeeded.ShouldBeTrue(output);
            output.ShouldContain("as a Mermaid eventmodeling diagram to");
            var text = (await File.ReadAllTextAsync(path, TestContext.Current.CancellationToken)).ReplaceLineEndings("\n");
            text.ShouldStartWith("---\ntitle: \"mermaid-6007\"\n---\neventmodeling\n");
            text.ShouldContain("%% RenameThing (no pattern)\n    rf 01 cmd RenameThing\n");
            Directory.GetFiles(directory).Select(Path.GetFileName).ShouldBe([EventModelInput.DefaultMermaidFile]);
        }
        finally
        {
            Directory.Delete(directory, true);
        }
    }

    [Fact]
    public async Task format_mermaid_does_not_go_with_url_or_json()
    {
        var (succeeded, output) = await runAsync(new EventModelCommand(),
            new EventModelInput { FormatFlag = "mermaid", UrlFlag = "http://localhost:5525" });
        succeeded.ShouldBeFalse();
        output.ShouldContain("--url does not go with --format mermaid");

        (succeeded, output) = await runAsync(new EventModelCommand(),
            new EventModelInput { FormatFlag = "mermaid", JsonFlag = "model.json" });
        succeeded.ShouldBeFalse();
        output.ShouldContain("name the diagram's file with --output");

        (succeeded, output) = await runAsync(new EventModelCommand(), new EventModelInput { FormatFlag = "yaml" });
        succeeded.ShouldBeFalse();
        output.ShouldContain("'yaml' is not a format this command writes");
    }

    [Fact]
    public void output_names_the_json_file_too()
    {
        new EventModelInput { OutputFlag = "out/model.json" }.ResolveJsonPath().ShouldBe("out/model.json");
        new EventModelInput { FormatFlag = "mermaid" }.ResolveMermaidPath().ShouldBe(EventModelInput.DefaultMermaidFile);
        new EventModelInput { FormatFlag = "Mermaid", OutputFlag = "docs/model.mmd" }.ResolveMermaidPath().ShouldBe("docs/model.mmd");
    }

    [Fact]
    public async Task describe_event_model_mermaid_prints_the_diagram_instead_of_the_report()
    {
        var input = new DescribeEventModelInput { HostBuilder = hostBuilder(), MermaidFlag = true };
        var (succeeded, output) = await runAsync(new DescribeEventModelCommand(), input);

        succeeded.ShouldBeTrue(output);
        // The host logs its discovery to the console too, so the diagram follows the log lines
        output.ReplaceLineEndings("\n").ShouldContain("---\ntitle: \"mermaid-6007\"\n---\neventmodeling\n");
        output.ShouldContain("cmd RenameThing");
        output.ShouldNotContain("Event Model 'mermaid-6007'");
    }

    [Fact]
    public async Task describe_event_model_mermaid_honours_the_report_filters()
    {
        var input = new DescribeEventModelInput { HostBuilder = hostBuilder(), MermaidFlag = true, ChapterFlag = "Nowhere" };
        var (succeeded, output) = await runAsync(new DescribeEventModelCommand(), input);

        succeeded.ShouldBeTrue(output);
        output.ShouldNotContain("RenameThing");
    }
}

public record RenameThing(Guid Id, string Name);

public record ThingRenamed(Guid Id, string Name);

public static class RenameThingHandler
{
    public static ThingRenamed Handle(RenameThing command) => new(command.Id, command.Name);
}
