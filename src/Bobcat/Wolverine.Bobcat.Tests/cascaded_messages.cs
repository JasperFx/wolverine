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
        report.Columns.ShouldBe(new[]
        {
            "at (ms)", "event", "message", "destination", "attempt", "service",
            "message id", "correlation id", "causation id", "saga id", "trace id", "activity id"
        });
        report.Cells.ShouldContain(x => x.Name == "message" && x.DisplayText == nameof(SendConfirmationEmail));

        var events = recording.Reports.OfType<AppendedEventsReport>().Single();
        events.Cells.ShouldContain(x => x.Name == "event" && x.DisplayText == nameof(AppointmentConfirmed));
    }

    // GH-5106: every row carries the ids the rest of the system's telemetry is keyed on, and they join up --
    // the act's correlation id throughout, and a cascaded message names the act's message as its cause
    [Fact]
    public async Task the_message_activity_carries_the_ids_that_join_it_to_the_telemetry()
    {
        var recording = await Recordings.RecordAsync(async () => await confirmedAppointment());

        var report = recording.Reports.OfType<MessageActivityReport>().Single();
        var rows = report.Cells.Chunk(report.Columns.Count)
            .Select(row => row.ToDictionary(x => x.Name, x => x.DisplayText))
            .ToList();

        var act = rows.First(x => x["message"] == $"{nameof(ConfirmAppointment)} (the act's own)");
        Guid.TryParse(act["message id"], out var actId).ShouldBeTrue();
        act["correlation id"].ShouldBe(ActCorrelationId);
        act["causation id"].ShouldBe("");

        var cascaded = rows.Where(x => x["message"] == nameof(SendConfirmationEmail)).ToList();
        cascaded.ShouldNotBeEmpty();
        foreach (var row in cascaded)
        {
            row["correlation id"].ShouldBe(ActCorrelationId);
            row["causation id"].ShouldBe(actId.ToString());
            row["message id"].ShouldNotBe(act["message id"]);
        }
    }
}
