using Bobcat;
using Bobcat.Engine;
using Marten;
using Microsoft.Extensions.DependencyInjection;

namespace Wolverine.Bobcat.Tests;

/// <summary>A singleton view: one document, which no identity in a spec names.</summary>
public record Dashboard(Guid Id, int OpenAppointments, string Status);

// The read-model shapes an event model's examples take that an id alone cannot reach: a view given
// directly, a singleton view, and a view that does not exist at all (bobcat#423 round 2)
[Collection(nameof(AppointmentsCollection))]
public class modeled_read_models(AppointmentsHost app) : WolverineSpec(app.Host), IAsyncLifetime
{
    // Nothing but these specs makes a Dashboard, so each starts with none
    public async ValueTask InitializeAsync()
        => await app.Host.Services.GetRequiredService<IDocumentStore>().Advanced.Clean.DeleteDocumentsByTypeAsync(typeof(Dashboard));

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;

    [Fact]
    public async Task a_read_model_given_directly_is_stored_and_can_be_read_back()
    {
        var theDashboard = Guid.NewGuid();
        await GivenReadModel<Dashboard>(new Dashboard(theDashboard, 3, "busy"));

        var dashboard = await ThenReadModel<Dashboard>(theDashboard, Specify<Dashboard>().With(x => x.OpenAppointments, 3));
        dashboard.Status.ShouldBe("busy");
    }

    [Fact]
    public async Task a_partial_read_model_given_directly_fills_what_it_does_not_name()
    {
        await GivenReadModel<Dashboard>(Specify<Dashboard>().With(x => x.Status, "quiet"));

        var dashboard = await ThenSingleReadModel<Dashboard>(Specify<Dashboard>().With(x => x.Status, "quiet"));
        dashboard.Id.ShouldNotBe(Guid.Empty);
    }

    [Fact]
    public async Task the_given_step_reads_as_only_what_the_partial_names()
    {
        var recording = await Recordings.RecordAsync(async () =>
        {
            await GivenReadModel<Dashboard>(Specify<Dashboard>().With(x => x.Status, "quiet"));
        });

        recording.Steps.First().Text.ShouldBe("the Dashboard read model is Dashboard(Status: quiet)");
    }

    [Fact]
    public async Task a_given_read_model_of_the_wrong_type_is_refused_by_name()
    {
        (await Should.ThrowAsync<SpecCriticalException>(() => GivenReadModel<Dashboard>(Specify<Appointment>())))
            .Message.ShouldContain("cannot be stored as the Dashboard read model");
    }

    [Fact]
    public async Task a_single_read_model_fails_when_there_are_two()
    {
        await GivenReadModel<Dashboard>(Specify<Dashboard>());
        await GivenReadModel<Dashboard>(Specify<Dashboard>());

        (await Should.ThrowAsync<SpecificationFailedException>(() => ThenSingleReadModel<Dashboard>()))
            .Message.ShouldBe("Expected exactly one Dashboard document, but there are 2.");
    }

    [Fact]
    public async Task a_single_read_model_fails_when_there_is_none()
    {
        (await Should.ThrowAsync<SpecificationFailedException>(() => ThenSingleReadModel<Dashboard>()))
            .Message.ShouldBe("Expected exactly one Dashboard document, but there are none.");
    }

    [Fact]
    public async Task no_read_model_at_all()
    {
        var recording = await Recordings.RecordAsync(async () => await ThenNoReadModel<Dashboard>());

        recording.GatheredFailures().ShouldBeNull();
        recording.Steps.Last().Text.ShouldBe("there is no Dashboard");
    }

    [Fact]
    public async Task no_read_model_at_all_fails_when_there_is_one()
    {
        await GivenReadModel<Dashboard>(Specify<Dashboard>());

        (await Should.ThrowAsync<SpecificationFailedException>(() => ThenNoReadModel<Dashboard>()))
            .Message.ShouldStartWith("Expected no Dashboard document, but there is one");
    }
}

// An event model's refusal can carry values (EmailAlreadyInUse { email }): the refusal names them
[Collection(nameof(AppointmentsCollection))]
public class refusals_naming_values(AppointmentsHost app) : WolverineSpec(app.Host)
{
    private static readonly Patient ann = new("Ann", new Address("Austin"));

    private async Task alreadyConfirmed()
    {
        var theAppointment = Guid.NewGuid();
        await GivenEvents<Appointment>(theAppointment, new AppointmentScheduled(theAppointment, ann, []), new AppointmentConfirmed(theAppointment, DateTimeOffset.UtcNow));
        await WhenReceived(new ConfirmAppointment(theAppointment));
    }

    [Fact]
    public async Task a_refusal_naming_every_value_passes()
    {
        var recording = await Recordings.RecordAsync(async () =>
        {
            await alreadyConfirmed();
            ThenRefusedWith("already confirmed", "appointment");
        });

        recording.GatheredFailures().ShouldBeNull();
        recording.Steps.Last().Text.ShouldBe("refused with \"already confirmed\", naming appointment");
    }

    [Fact]
    public async Task a_refusal_missing_a_value_fails_on_that_value()
    {
        var recording = await Recordings.RecordAsync(async () =>
        {
            await alreadyConfirmed();
            ThenRefusedWith("already confirmed", "appointment", "Bob");
        });

        var then = recording.Steps.Last();
        then.Status.ShouldBe(ResultStatus.failed);
        then.Cells.Single(x => x.Name == "Bob").Status.ShouldBe(ResultStatus.failed);
        then.Cells.Single(x => x.Name == "appointment").Status.ShouldBe(ResultStatus.success);
    }

    [Fact]
    public async Task with_no_values_it_is_the_plain_refusal()
    {
        var recording = await Recordings.RecordAsync(async () =>
        {
            await alreadyConfirmed();
            ThenRefusedWith("already confirmed", []);
        });

        recording.Steps.Last().Text.ShouldBe("refused with \"already confirmed\"");
    }
}

// The typed refusal: the exception a handler throws, matched on the members a spec names
[Collection(nameof(AppointmentsCollection))]
public class typed_refusals(AppointmentsHost app) : WolverineSpec(app.Host)
{
    private static readonly Patient ann = new("Ann", new Address("Austin"));

    private async Task<Guid> alreadyConfirmed()
    {
        var theAppointment = Guid.NewGuid();
        await GivenEvents<Appointment>(theAppointment, new AppointmentScheduled(theAppointment, ann, []),
            new AppointmentConfirmed(theAppointment, DateTimeOffset.UtcNow));
        await WhenReceived(new ConfirmAppointment(theAppointment));
        return theAppointment;
    }

    [Fact]
    public async Task the_refusal_is_the_exception_type()
    {
        await alreadyConfirmed();

        var thrown = ThenRefusedWith<AppointmentAlreadyConfirmed>();
        thrown.ShouldNotBeNull();
    }

    [Fact]
    public async Task the_refusal_matches_the_members_a_partial_names()
    {
        var recording = await Recordings.RecordAsync(async () =>
        {
            var theAppointment = await alreadyConfirmed();
            ThenRefusedWith<AppointmentAlreadyConfirmed>(
                Specify<AppointmentAlreadyConfirmed>().With(x => x.AppointmentId, theAppointment));
        });

        recording.GatheredFailures().ShouldBeNull();
        recording.Steps.Last().Text.ShouldBe("refused with AppointmentAlreadyConfirmed(AppointmentId: theAppointment)");
    }

    [Fact]
    public async Task a_member_that_disagrees_is_a_failed_cell()
    {
        var recording = await Recordings.RecordAsync(async () =>
        {
            await alreadyConfirmed();
            ThenRefusedWith<AppointmentAlreadyConfirmed>(
                Specify<AppointmentAlreadyConfirmed>().With(x => x.AppointmentId, Guid.NewGuid()));
        });

        var then = recording.Steps.Last();
        then.Status.ShouldBe(ResultStatus.failed);
        then.Cells.Single(x => x.Name == nameof(AppointmentAlreadyConfirmed.AppointmentId)).Status.ShouldBe(ResultStatus.failed);
    }

    [Fact]
    public async Task the_wrong_exception_type_fails_naming_what_was_thrown()
    {
        var recording = await Recordings.RecordAsync(async () =>
        {
            await alreadyConfirmed();
            ThenRefusedWith<ArgumentException>();
        });

        recording.Steps.Last().Status.ShouldBe(ResultStatus.failed);
        recording.GatheredFailures()!.ShouldContain("but the act threw AppointmentAlreadyConfirmed");
    }

    [Fact]
    public async Task an_act_that_succeeded_was_not_refused()
    {
        var recording = await Recordings.RecordAsync(async () =>
        {
            var theAppointment = Guid.NewGuid();
            await GivenEvents<Appointment>(theAppointment, new AppointmentScheduled(theAppointment, ann, []));
            await WhenReceived(new ConfirmAppointment(theAppointment));
            ThenRefusedWith<AppointmentAlreadyConfirmed>();
        });

        recording.GatheredFailures()!.ShouldContain("but the act succeeded");
    }

    [Fact]
    public async Task the_text_refusal_still_reads_the_message()
    {
        await alreadyConfirmed();

        ThenRefusedWith("already confirmed");
    }
}

