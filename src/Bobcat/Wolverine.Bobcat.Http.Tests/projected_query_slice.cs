using Bobcat;
using Bobcat.Generated.EventModel;
using Bobcat.Xunit;
using JasperFx.Events.EventModeling;

namespace Wolverine.Bobcat.Http.Tests;

// bobcat#501: WhenReading<T> carries a GET trigger and a ReadModel role and no Command role, so a spec whose act it is
// reaches the Event Model as a Query slice that reads T, with the route it was given
[BobcatFeature("Reading an appointment over HTTP")]
[Collection(nameof(AppCollection))]
public class projected_query_slice(AppHost app) : WolverineHttpSpec(app.Host)
{
    [Fact]
    public async Task a_scheduled_appointment_is_read()
    {
        var id = Guid.NewGuid();
        await GivenEvents<Appointment>(id, new AppointmentScheduled(id, "Ann"));

        var appointment = await WhenReading<Appointment>("/api/appointments/{id}", id);

        appointment.ShouldNotBeNull().Patient.ShouldBe("Ann");
    }

    [Fact]
    public void the_slice_is_a_query_that_reads_the_read_model_through_its_route()
    {
        var slice = BobcatEventModelSource.Describe().Slices.Single(x => x.Name == "Reading an appointment over HTTP");

        slice.Pattern.ShouldBe(SlicePattern.Query);
        slice.CommandType.ShouldBeNull();
        slice.ReadsFrom.Select(x => x.Name).ShouldBe([nameof(Appointment)]);
        slice.TriggerKind.ShouldBe(TriggerKind.Http);
        slice.TriggerOrigin.ShouldNotBeNull().HttpRoute.ShouldBe("/api/appointments/{id}");
        slice.TriggerOrigin.HttpMethod.ShouldBe("GET");
    }
}

// The route-name read: the generator resolves the constant name to the endpoint's template, and stamps both
[BobcatFeature("Reading an appointment summary by route name")]
[Collection(nameof(AppCollection))]
public class projected_query_slice_by_route_name(AppHost app) : WolverineHttpSpec(app.Host)
{
    [Fact]
    public async Task a_scheduled_appointments_summary_is_read()
    {
        var id = Guid.NewGuid();
        await GivenEvents<Appointment>(id, new AppointmentScheduled(id, "Ann"));

        var summary = await WhenReadingRoute<AppointmentSummary>("AppointmentSummary", id);

        summary.ShouldBe(new AppointmentSummary(id, "Ann", false));
    }

    [Fact]
    public void the_slice_carries_the_route_and_the_route_name()
    {
        var slice = BobcatEventModelSource.Describe().Slices.Single(x => x.Name == "Reading an appointment summary by route name");

        slice.Pattern.ShouldBe(SlicePattern.Query);
        slice.ReadsFrom.Select(x => x.Name).ShouldBe([nameof(AppointmentSummary)]);
        slice.TriggerOrigin.ShouldNotBeNull().HttpRoute.ShouldBe("/api/appointments/{id}/summary");
        slice.TriggerOrigin.HttpRouteName.ShouldBe("AppointmentSummary");
        slice.TriggerOrigin.HttpMethod.ShouldBe("GET");
    }
}

// A PUT command act by route name is a Command slice with the verb, the route and the route name
[BobcatFeature("Renaming a patient over HTTP")]
[Collection(nameof(AppCollection))]
public class projected_put_slice_by_route_name(AppHost app) : WolverineHttpSpec(app.Host)
{
    [Fact]
    public async Task a_patient_is_renamed()
    {
        var id = Guid.NewGuid();
        await GivenEvents<Appointment>(id, new AppointmentScheduled(id, "Ann"));

        await WhenPutToRoute(new RenamePatient(id, "Bea"), "RenamePatient");

        ThenEvents(new PatientRenamed(id, "Bea"));
    }

    [Fact]
    public void the_slice_is_a_command_with_the_put_route_and_its_name()
    {
        var slice = BobcatEventModelSource.Describe().Slices.Single(x => x.Name == "Renaming a patient over HTTP");

        slice.Pattern.ShouldBe(SlicePattern.Command);
        slice.CommandType.ShouldNotBeNull().Name.ShouldBe(nameof(RenamePatient));
        slice.TriggerOrigin.ShouldNotBeNull().HttpRoute.ShouldBe("/api/appointments/{appointmentId}/patient");
        slice.TriggerOrigin.HttpRouteName.ShouldBe("RenamePatient");
        slice.TriggerOrigin.HttpMethod.ShouldBe("PUT");
    }
}
