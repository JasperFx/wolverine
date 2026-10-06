using Bobcat.Engine;

namespace Wolverine.Bobcat.Tests;

// GH-4835: ThenEvents(params object[]) compares structurally, renders a cell per leaf, and shows an
// ignored member's value rather than dropping it.
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
    public async Task a_nested_mismatch_is_one_failed_cell_named_by_its_path()
    {
        var id = Guid.NewGuid();

        var recording = await Recordings.RecordAsync(async () =>
        {
            await WhenReceived(new ScheduleAppointment(id, ann, []));
            ThenEvents(new AppointmentScheduled(id, ann with { Address = new Address("Boston") }, []));
        });

        var then = recording.Steps.Last();
        then.Status.ShouldBe(ResultStatus.failed);

        var city = then.Cells.Single(x => x.Name == "Patient.Address.City");
        city.Status.ShouldBe(ResultStatus.failed);
        city.Expected.ShouldBe("Boston");
        city.Actual.ShouldBe("Austin");

        then.Cells.Single(x => x.Name == "Patient.Name").Status.ShouldBe(ResultStatus.success);
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

        var confirmedAt = recording.Steps.Last().Cells.Single(x => x.Name == "ConfirmedAt");
        confirmedAt.Status.ShouldBe(ResultStatus.ok);
        confirmedAt.Expected.ShouldBeNull();
        confirmedAt.DisplayText.ShouldNotBe("NULL");
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
    public async Task a_missing_or_extra_event_is_reported_by_position()
    {
        var id = Guid.NewGuid();
        await WhenReceived(new ScheduleAppointment(id, ann, []));

        Should.Throw<SpecificationFailedException>(() => ThenEvents(
                new AppointmentScheduled(id, ann, []),
                new AppointmentConfirmed(id, default)))
            .Message.ShouldContain("[1] AppointmentConfirmed is missing");
    }
}
