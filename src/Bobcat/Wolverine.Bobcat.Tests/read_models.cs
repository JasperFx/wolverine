using Bobcat;
using Bobcat.Engine;

namespace Wolverine.Bobcat.Tests;

// A view's example in an event model is a read model judged on the fields it names, or one that
// should not exist at all (bobcat#423 found both missing while generating specs from emlang)
[Collection(nameof(AppointmentsCollection))]
public class read_models(AppointmentsHost app) : WolverineSpec(app.Host)
{
    private static readonly Patient ann = new("Ann", new Address("Austin"));

    [Fact]
    public async Task a_read_model_is_judged_on_only_the_members_a_partial_names()
    {
        var theAppointment = Guid.NewGuid();
        await GivenEvents<Appointment>(theAppointment, new AppointmentScheduled(theAppointment, ann, []));
        await WhenReceived(new ConfirmAppointment(theAppointment));

        var appointment = await ThenReadModel<Appointment>(theAppointment,
            Specify<Appointment>().With(x => x.Patient, "Ann").With(x => x.Confirmed, true));

        appointment.Id.ShouldBe(theAppointment);
    }

    [Fact]
    public async Task a_read_model_that_disagrees_names_the_member()
    {
        var theAppointment = Guid.NewGuid();
        await GivenEvents<Appointment>(theAppointment, new AppointmentScheduled(theAppointment, ann, []));

        var recording = await Recordings.RecordAsync(async () =>
        {
            await ThenReadModel<Appointment>(theAppointment, Specify<Appointment>().With(x => x.Confirmed, true));
        });

        var then = recording.Steps.Last();
        then.Text.ShouldBe("the Appointment matches");
        then.Status.ShouldBe(ResultStatus.failed);
        var confirmed = then.Cells.Single(x => x.Name == nameof(Appointment.Confirmed));
        confirmed.Status.ShouldBe(ResultStatus.failed);
        then.Cells.ShouldNotContain(x => x.Name == nameof(Appointment.Patient));
    }

    [Fact]
    public async Task no_read_model_when_nothing_created_one()
    {
        var theAppointment = Guid.NewGuid();

        var recording = await Recordings.RecordAsync(async () =>
        {
            await ThenNoReadModel<Appointment>(theAppointment);
        });

        recording.GatheredFailures().ShouldBeNull();
        recording.Steps.Last().Text.ShouldBe("there is no Appointment theAppointment");
    }

    [Fact]
    public async Task no_read_model_fails_when_there_is_one()
    {
        var theAppointment = Guid.NewGuid();
        await GivenEvents<Appointment>(theAppointment, new AppointmentScheduled(theAppointment, ann, []));

        (await Should.ThrowAsync<SpecificationFailedException>(() => ThenNoReadModel<Appointment>(theAppointment)))
            .Message.ShouldStartWith("Expected no Appointment document");
    }
}
