using Bobcat.Engine;

namespace Wolverine.Bobcat.Tests;

// GH-4835: ThenEvents(params object[]) compares structurally and reads as a set verification: one row
// per event — OK, a FAIL naming the values that disagree, MISSING, EXTRA or ORDER — and shows an
// ignored member's actual value rather than dropping it.
[Collection(nameof(AppointmentsCollection))]
public class structural_event_values(AppointmentsHost app) : WolverineSpec(app.Host)
{
    private static readonly Patient ann = new("Ann", new Address("Austin"));

    [Fact]
    public async Task a_record_holding_a_list_matches_structurally_where_Equals_would_not()
    {
        var id = Guid.NewGuid();
        await WhenReceived(new ScheduleAppointment(id, ann, ["first visit", "bring insurance card"]));

        // A different List instance: record equality compares it by reference and says false
        var expected = new AppointmentScheduled(id, ann, ["first visit", "bring insurance card"]);
        expected.Equals(TheEvent<AppointmentScheduled>()).ShouldBeFalse();

        ThenEvents(expected);
    }

    [Fact]
    public async Task a_nested_mismatch_is_one_failed_row_naming_the_path_that_disagreed()
    {
        var id = Guid.NewGuid();

        var recording = await Recordings.RecordAsync(async () =>
        {
            await WhenReceived(new ScheduleAppointment(id, ann, []));
            ThenEvents(new AppointmentScheduled(id, ann with { Address = new Address("Boston") }, []));
        });

        var then = recording.Steps.Last();
        then.Status.ShouldBe(ResultStatus.failed);

        // The right event with one wrong value: the event matched, and only the leaf that disagreed is named
        then.Cells.Single(x => x.Name == "event").Status.ShouldBe(ResultStatus.success);

        var values = then.Cells.Single(x => x.Name == "values");
        values.Status.ShouldBe(ResultStatus.failed);
        values.Expected.ShouldBe("Patient.Address.City: Boston");
        values.Actual.ShouldBe("Patient.Address.City: Austin");

        then.Cells.ShouldAllBe(x => x.RowIndex == 0);
    }

    [Fact]
    public async Task an_ignored_member_is_shown_as_a_value_not_judged()
    {
        var id = Guid.NewGuid();
        await GivenEvents<Appointment>(id, new AppointmentScheduled(id, ann, []));

        var recording = await Recordings.RecordAsync(async () =>
        {
            await WhenReceived(new ConfirmAppointment(id));
            ThenEvents(Expect.Value(new AppointmentConfirmed(id, default)).Ignoring(x => x.ConfirmedAt));
        });

        recording.GatheredFailures().ShouldBeNull();

        // The row shows what happened, so the ignored timestamp reads as its real value, not default
        var values = recording.Steps.Last().Cells.Single(x => x.Name == "values");
        values.Status.ShouldBe(ResultStatus.success);
        values.DisplayText.ShouldContain("ConfirmedAt: ");
        values.DisplayText.ShouldNotContain("0001-01-01");
    }

    [Fact]
    public async Task without_the_ignore_a_minted_value_fails()
    {
        var id = Guid.NewGuid();
        await GivenEvents<Appointment>(id, new AppointmentScheduled(id, ann, []));
        await WhenReceived(new ConfirmAppointment(id));

        Should.Throw<SpecificationFailedException>(() => ThenEvents(new AppointmentConfirmed(id, default)))
            .Message.ShouldContain("ConfirmedAt");
    }

    [Fact]
    public async Task a_missing_event_is_missing_and_the_events_around_it_still_match()
    {
        var id = Guid.NewGuid();
        await WhenReceived(new ScheduleAppointment(id, ann, []));

        // Written FIRST, so a positional comparison would have called the AppointmentScheduled wrong too
        var message = Should.Throw<SpecificationFailedException>(() => ThenEvents(
                new AppointmentConfirmed(id, default),
                new AppointmentScheduled(id, ann, [])))
            .Message;

        message.ShouldStartWith("MISSING AppointmentConfirmed(");
        message.ShouldNotContain("AppointmentScheduled");
    }

    [Fact]
    public async Task an_event_nobody_expected_is_extra()
    {
        var id = Guid.NewGuid();
        await GivenEvents<Appointment>(id, new AppointmentScheduled(id, ann, []));
        await WhenReceived(new ConfirmAppointment(id));

        Should.Throw<SpecificationFailedException>(() => ThenEvents(Array.Empty<object>()))
            .Message.ShouldStartWith("EXTRA AppointmentConfirmed(");
    }
}
