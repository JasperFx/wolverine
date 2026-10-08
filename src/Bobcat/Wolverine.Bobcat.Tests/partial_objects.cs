using Bobcat;
using Bobcat.Engine;
using Bobcat.Runtime;

namespace Wolverine.Bobcat.Tests;

// wolverine#4870: partial objects (bobcat#416) in every step. A Given or a When builds one, filling
// what it leaves unspecified; a Then judges and shows only the members it names.
[Collection(nameof(AppointmentsCollection))]
public class partial_objects(AppointmentsHost app) : WolverineSpec(app.Host)
{
    private static readonly Patient ann = new("Ann", new Address("Austin"));

    private async Task<Guid> aScheduledAppointment()
    {
        var id = Guid.NewGuid();
        await GivenEvents<Appointment>(id, new AppointmentScheduled(id, ann, []));
        return id;
    }

    // ---- arrange and act -----------------------------------------------------------------------

    [Fact]
    public async Task a_given_event_is_built_from_only_the_members_the_spec_is_about()
    {
        var id = Guid.NewGuid();

        var recording = await Recordings.RecordAsync(async () =>
        {
            // Notes is never mentioned: the build fills it with an empty list
            await GivenEvents<Appointment>(id, Specify<AppointmentScheduled>()
                .With(x => x.AppointmentId, id)
                .With(x => x.Patient, ann));
        });

        recording.GatheredFailures().ShouldBeNull();

        var given = recording.Steps.Single();
        given.Text.ShouldContain("AppointmentScheduled(AppointmentId: ");
        given.Text.ShouldNotContain("Notes");

        (await TheAggregate<Appointment>(id))!.Patient.ShouldBe("Ann");
    }

    [Fact]
    public async Task a_given_event_can_be_a_vertical_table()
    {
        var id = Guid.NewGuid();

        await GivenEvents<Appointment>(id, PartialObjects.FromTable(typeof(AppointmentScheduled), $"""
            | field                                | value |
            | {nameof(AppointmentScheduled.AppointmentId)} | {id}  |
            """));

        (await TheAggregate<Appointment>(id))!.Id.ShouldBe(id);
    }

    [Fact]
    public async Task tables_and_single_values_mix_in_one_given()
    {
        var first = Guid.NewGuid();
        var second = Guid.NewGuid();

        // A horizontal table is one event per row; a list of partials is spread among the other values
        await GivenEvents<Appointment>(first,
            PartialObjects.FromTable(typeof(AppointmentScheduled), $"""
                | {nameof(AppointmentScheduled.AppointmentId)} |
                | {first}                                      |
                """),
            new AppointmentConfirmed(first, DateTimeOffset.UtcNow));

        (await TheAggregate<Appointment>(first))!.Confirmed.ShouldBeTrue();
        second.ShouldNotBe(first);
    }

    [Fact]
    public async Task the_fill_policy_is_the_scenarios_to_choose()
    {
        var id = Guid.NewGuid();
        UnspecifiedValues = new PatientsAreNamedPat();

        try
        {
            await GivenEvents<Appointment>(id, Specify<AppointmentScheduled>().With(x => x.AppointmentId, id));
        }
        finally
        {
            UnspecifiedValues = null;
        }

        (await TheAggregate<Appointment>(id))!.Patient.ShouldBe("Pat");
    }

    private class PatientsAreNamedPat : IUnspecifiedValues
    {
        public bool TryValueFor(UnspecifiedMember member, out object? value)
        {
            value = member.Path == "Patient.Name" ? "Pat" : null;
            return value is not null || PredictableValues.Instance.TryValueFor(member, out value);
        }
    }

    [Fact]
    public async Task a_received_message_can_be_partial_and_its_step_shows_only_what_was_specified()
    {
        var id = Guid.NewGuid();

        var recording = await Recordings.RecordAsync(async () =>
        {
            await WhenReceived(Specify<ScheduleAppointment>()
                .With(x => x.AppointmentId, id)
                .With(x => x.Patient, Specify<Patient>().With(x => x.Name, "Ann")));
            ThenEvents(Specify<AppointmentScheduled>().With(x => x.AppointmentId, id));
        });

        recording.GatheredFailures().ShouldBeNull();

        var when = recording.Steps.First();
        when.Text.ShouldStartWith("ScheduleAppointment(AppointmentId: ");
        when.Text.ShouldNotContain("Notes");

        TheEvent<AppointmentScheduled>().Patient.Name.ShouldBe("Ann");
    }

    [Fact]
    public async Task a_partial_the_type_cannot_take_fails_the_spec_not_the_act()
    {
        // Not a constant, so BOBCAT032 cannot catch it at compile time
        var misspelled = string.Concat("Appointment", "Idd");

        var ex = await Should.ThrowAsync<Exception>(() =>
            WhenReceived(Specify<ScheduleAppointment>().With(misspelled, Guid.NewGuid())));

        ex.Message.ShouldContain("AppointmentIdd");
        LastAct.ShouldBe(ActOutcome.None);
    }

    // ---- ThenEvents with partials ---------------------------------------------------------------

    [Fact]
    public async Task a_partial_event_is_judged_and_shown_on_only_the_members_it_names()
    {
        var id = await aScheduledAppointment();

        var recording = await Recordings.RecordAsync(async () =>
        {
            await WhenReceived(new CheckInPatient(id, "Front desk", 20m));
            ThenEvents(
                Specify<PatientCheckedIn>().With(x => x.Desk, "Back desk"),
                Specify<CopayCollected>().With(x => x.Amount, 20m));
        });

        var then = recording.Steps.Last();
        then.Text.ShouldBe("PatientCheckedIn, CopayCollected are emitted");
        then.Status.ShouldBe(ResultStatus.failed);

        var values = then.Cells.Where(x => x.Name == ObjectSetVerification.ValuesColumn).OrderBy(x => x.RowIndex).ToArray();
        values[0].Status.ShouldBe(ResultStatus.failed);
        values[0].Expected.ShouldBe("Desk: Back desk");
        values[0].Actual.ShouldBe("Desk: Front desk");

        // The minted CheckedInAt is never specified, so it is neither judged nor shown
        values[1].Status.ShouldBe(ResultStatus.success);
        values[1].DisplayText.ShouldBe("Amount: 20");
    }

    [Fact]
    public async Task then_events_still_means_in_this_order()
    {
        var id = await aScheduledAppointment();
        await WhenReceived(new CheckInPatient(id, "Front desk", 20m));

        Should.Throw<SpecificationFailedException>(() => ThenEvents(
                Specify<CopayCollected>().With(x => x.Amount, 20m),
                Specify<PatientCheckedIn>().With(x => x.Desk, "Front desk")))
            .Message.ShouldContain("ORDER");
    }

    // ---- ThenEventsInAnyOrder ---------------------------------------------------------------------

    [Fact]
    public async Task in_any_order_accepts_the_same_events_written_the_other_way_round()
    {
        var id = await aScheduledAppointment();
        await WhenReceived(new CheckInPatient(id, "Front desk", 20m));

        ThenEventsInAnyOrder(
            Specify<CopayCollected>().With(x => x.Amount, 20m),
            Specify<PatientCheckedIn>().With(x => x.Desk, "Front desk"));

        ThenEventsInAnyOrder(typeof(CopayCollected), typeof(PatientCheckedIn));
    }

    [Fact]
    public async Task in_any_order_is_still_exactly_these_events()
    {
        var id = await aScheduledAppointment();
        await WhenReceived(new CheckInPatient(id, "Front desk", 20m));

        Should.Throw<SpecificationFailedException>(() =>
                ThenEventsInAnyOrder(Specify<CopayCollected>().With(x => x.Amount, 20m)))
            .Message.ShouldStartWith("EXTRA PatientCheckedIn(");
    }

    // ---- ThenEmitted / ThenNotEmitted -----------------------------------------------------------

    [Fact]
    public async Task emitted_finds_an_event_among_the_others()
    {
        var id = await aScheduledAppointment();

        var recording = await Recordings.RecordAsync(async () =>
        {
            await WhenReceived(new CheckInPatient(id, "Front desk", 20m));
            ThenEmitted(Specify<CopayCollected>().With(x => x.Amount, 20m));
            ThenEmitted<PatientCheckedIn>();
        });

        recording.GatheredFailures().ShouldBeNull();

        // Only the expected event is shown; the PatientCheckedIn beside it is not EXTRA
        var emitted = recording.Step("CopayCollected is emitted");
        emitted.Cells.Where(x => x.Name == "event").ShouldHaveSingleItem();
        emitted.Cells.ShouldNotContain(x => x.Name == "extra-row");
    }

    [Fact]
    public async Task emitted_with_the_wrong_values_is_a_failed_row()
    {
        var id = await aScheduledAppointment();
        await WhenReceived(new CheckInPatient(id, "Front desk", 20m));

        Should.Throw<SpecificationFailedException>(() => ThenEmitted(Specify<CopayCollected>().With(x => x.Amount, 30m)))
            .Message.ShouldBe("FAIL CopayCollected: expected Amount: 30, was Amount: 20");

        Should.Throw<SpecificationFailedException>(() => ThenEmitted<AppointmentConfirmed>())
            .Message.ShouldStartWith("MISSING AppointmentConfirmed");
    }

    [Fact]
    public async Task not_emitted_by_type()
    {
        var id = await aScheduledAppointment();
        await WhenReceived(new CheckInPatient(id, "Front desk", 20m));

        ThenNotEmitted<AppointmentConfirmed>();

        Should.Throw<SpecificationFailedException>(() => ThenNotEmitted<CopayCollected>())
            .Message.ShouldStartWith("PRESENT CopayCollected(");
    }

    [Fact]
    public async Task not_emitted_with_a_partial_forbids_only_the_matching_events()
    {
        var id = await aScheduledAppointment();
        await WhenReceived(new CheckInPatient(id, "Front desk", 20m));

        ThenNotEmitted(Specify<CopayCollected>().With(x => x.Amount, 0m));

        Should.Throw<SpecificationFailedException>(() =>
                ThenNotEmitted(Specify<CopayCollected>().With(x => x.Amount, 20m)))
            .Message.ShouldContain("was not expected");
    }

    [Fact]
    public async Task an_act_that_failed_says_so_rather_than_missing()
    {
        var id = Guid.NewGuid();
        await GivenEvents<Appointment>(id, new AppointmentScheduled(id, ann, []), new AppointmentConfirmed(id, DateTimeOffset.UtcNow));

        // Confirming twice throws in the handler
        await WhenReceived(new ConfirmAppointment(id));

        Should.Throw<SpecificationFailedException>(() => ThenEmitted<AppointmentConfirmed>())
            .Message.ShouldStartWith("The act failed");
        Should.Throw<SpecificationFailedException>(() => ThenNotEmitted<AppointmentConfirmed>())
            .Message.ShouldStartWith("The act failed");
    }

    // ---- messages and any object ----------------------------------------------------------------

    [Fact]
    public async Task a_sent_message_can_be_partial()
    {
        var id = await aScheduledAppointment();
        await WhenReceived(new ConfirmAppointment(id));

        ThenMessageSent(Specify<SendConfirmationEmail>().With(x => x.To, "Ann"));

        Should.Throw<SpecificationFailedException>(() =>
                ThenMessageSent(Specify<SendConfirmationEmail>().With(x => x.To, "Bob")))
            .Message.ShouldContain("To: Ann");
    }

    [Fact]
    public async Task any_object_matches_a_partial_on_only_the_members_it_names()
    {
        var id = await aScheduledAppointment();
        await WhenReceived(new ConfirmAppointment(id));

        var appointment = await TheAggregate<Appointment>(id);
        ThenMatches(appointment, Specify<Appointment>().With(x => x.Confirmed, true));

        Should.Throw<SpecificationFailedException>(() =>
                ThenMatches(appointment, Specify<Appointment>().With(x => x.Patient, "Bob")))
            .Message.ShouldContain("Patient");
    }
}
