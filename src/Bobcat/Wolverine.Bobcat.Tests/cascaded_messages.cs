using Bobcat.Engine;

namespace Wolverine.Bobcat.Tests;

// GH-4837: assert what an act cascaded — by type, by value, by destination — and carry the tracked
// session's account of the scenario as a report written out on failure.
[Collection(nameof(AppointmentsCollection))]
public class cascaded_messages(AppointmentsHost app) : WolverineSpec(app.Host)
{
    private async Task<Guid> confirmedAppointment()
    {
        var id = Guid.NewGuid();
        await GivenEvents<Appointment>(id, new AppointmentScheduled(id, new Patient("Ann", new Address("Austin")), []));
        await WhenReceived(new ConfirmAppointment(id));
        return id;
    }

    [Fact]
    public async Task by_type_and_destination()
    {
        await confirmedAppointment();

        ThenMessageSent<SendConfirmationEmail>();
        ThenMessageSent<SendConfirmationEmail>("local://");
        ThenMessageSentLocally<SendConfirmationEmail>();
        ThenNoMessageSent<PatientArrived>();
    }

    [Fact]
    public async Task by_value()
    {
        var id = await confirmedAppointment();

        ThenMessageSent(new SendConfirmationEmail(id, "Ann"));
        Should.Throw<SpecificationFailedException>(() => ThenMessageSent(new SendConfirmationEmail(id, "Bob")))
            .Message.ShouldContain("To: expected Bob, was Ann");
    }

    [Fact]
    public async Task the_wrong_destination_names_where_it_went()
    {
        await confirmedAppointment();

        Should.Throw<SpecificationFailedException>(() => ThenMessageSent<SendConfirmationEmail>("rabbitmq://"))
            .Message.ShouldContain("local://");
        Should.Throw<SpecificationFailedException>(() => ThenMessageSentExternally<SendConfirmationEmail>());
    }

    [Fact]
    public async Task the_message_activity_rides_along_as_a_report_latched_to_failure()
    {
        var recording = await Recordings.RecordAsync(async () => await confirmedAppointment());

        var report = recording.Reports.OfType<MessageActivityReport>().Single();
        report.Visibility.ShouldBe(ReportVisibility.OnFailure);
        report.Columns.ShouldBe(new[] { "at (ms)", "event", "message", "destination", "attempt", "service" });
        report.Cells.ShouldContain(x => x.Name == "message" && x.DisplayText == nameof(SendConfirmationEmail));

        var events = recording.Reports.OfType<AppendedEventsReport>().Single();
        events.Cells.ShouldContain(x => x.Name == "event" && x.DisplayText == nameof(AppointmentConfirmed));
    }
}
