namespace Wolverine.Bobcat.Http.Tests;

// Read acts: GET a read model by template or by route name, inside the tracked session like every other act
[Collection(nameof(AppCollection))]
public class reading_over_http(AppHost app) : WolverineHttpSpec(app.Host)
{
    private async Task<Guid> scheduled(string patient = "Ann")
    {
        var theAppointment = Guid.NewGuid();
        await GivenEvents<Appointment>(theAppointment, new AppointmentScheduled(theAppointment, patient));
        return theAppointment;
    }

    [Fact]
    public async Task a_read_model_is_read_from_the_route_template_filled_with_its_values()
    {
        var id = await scheduled();

        var recording = await Recordings.RecordAsync(async () =>
        {
            var appointment = await WhenReading<Appointment>("/api/appointments/{id}", id);

            appointment.ShouldNotBeNull().Id.ShouldBe(id);
            appointment.Patient.ShouldBe("Ann");
            ThenResponseIs(200);
            ThenNoEvents();
        });

        recording.GatheredFailures().ShouldBeNull();

        // the step reads as the template; the filled URL rides in the HTTP exchange report
        var when = recording.Steps[0];
        when.Text.ShouldBe("Appointment is read from \"/api/appointments/{id}\"");
        when.Cells.Single(x => x.Name == "status").DisplayText.ShouldBe("200");
        recording.Reports.OfType<HttpExchangeReport>().Single().Cells
            .ShouldContain(x => x.Name == "route" && x.DisplayText == $"/api/appointments/{id}");
    }

    [Fact]
    public async Task a_read_model_is_read_by_route_name()
    {
        var id = await scheduled("Bea");

        var summary = await WhenReadingRoute<AppointmentSummary>("AppointmentSummary", id);

        summary.ShouldBe(new AppointmentSummary(id, "Bea", false));
        (await TheResponse<AppointmentSummary>()).ShouldBe(summary);
    }

    [Fact]
    public async Task a_missing_aggregate_is_not_found_and_reads_as_null()
    {
        var appointment = await WhenReading<Appointment>("/api/appointments/{id}", Guid.NewGuid());

        appointment.ShouldBeNull();
        ThenNotFound();
        LastAct.Error.ShouldBeNull();
    }

    [Fact]
    public async Task a_missing_document_is_not_found()
    {
        var summary = await WhenReadingRoute<AppointmentSummary>("AppointmentSummary", Guid.NewGuid());

        summary.ShouldBeNull();
        ThenNotFound();
    }

    [Fact]
    public async Task then_not_found_fails_when_something_was_found()
    {
        var id = await scheduled();
        await WhenReading<Appointment>("/api/appointments/{id}", id);

        Should.Throw<SpecificationFailedException>(() => ThenNotFound()).Message.ShouldContain("404");
    }

    [Fact]
    public async Task a_read_follows_a_write_through_the_same_view_of_the_store()
    {
        var id = await scheduled();

        await WhenPut(new RenamePatient(id, "Cal"));
        var appointment = await WhenReading<Appointment>("/api/appointments/{id}", id);

        appointment.ShouldNotBeNull().Patient.ShouldBe("Cal");
    }

    [Fact]
    public async Task route_values_must_match_the_template()
    {
        Should.Throw<ArgumentException>(() => HttpRoutes.Fill("/api/appointments/{id}"))
            .Message.ShouldContain("1 parameter(s), but 0 value(s)");

        (await Should.ThrowAsync<InvalidOperationException>(async () => await WhenReadingRoute<AppointmentSummary>("NoSuchRoute", Guid.NewGuid())))
            .Message.ShouldContain("No endpoint answering GET has the route name 'NoSuchRoute'");
    }
}

// PUT / PATCH / DELETE command acts, mirroring WhenPosted: the route from the request type, a template, or a route name
[Collection(nameof(AppCollection))]
public class other_command_verbs(AppHost app) : WolverineHttpSpec(app.Host)
{
    private async Task<Guid> scheduled()
    {
        var theAppointment = Guid.NewGuid();
        await GivenEvents<Appointment>(theAppointment, new AppointmentScheduled(theAppointment, "Ann"));
        return theAppointment;
    }

    [Fact]
    public async Task put_resolves_the_route_from_the_request_type()
    {
        var id = await scheduled();

        var recording = await Recordings.RecordAsync(async () =>
        {
            await WhenPut(new RenamePatient(id, "Bea"));
            ThenResponseIs(204);
            ThenEvents(new PatientRenamed(id, "Bea"));
        });

        recording.GatheredFailures().ShouldBeNull();
        recording.Steps[0].Text.ShouldBe($"RenamePatient(AppointmentId: theAppointment, Patient: \"Bea\") is put to \"/api/appointments/{id}/patient\"");
    }

    [Fact]
    public async Task put_by_route_name_fills_the_route_from_the_command()
    {
        var id = await scheduled();

        await WhenPutToRoute(new RenamePatient(id, "Bea"), "RenamePatient");

        ThenEvents(new PatientRenamed(id, "Bea"));
    }

    [Fact]
    public async Task patch_hands_its_response_back_through_the_response()
    {
        var id = await scheduled();

        await WhenPatched(new NoteAppointment(id, "bring x-rays"));

        ThenEvents(new AppointmentNoted(id, "bring x-rays"));
        (await TheResponse<AppointmentNoted>()).ShouldBe(new AppointmentNoted(id, "bring x-rays"));
    }

    [Fact]
    public async Task patch_by_route_and_by_route_name()
    {
        var id = await scheduled();

        await WhenPatched(new NoteAppointment(id, "one"), $"/api/appointments/{id}/notes");
        ThenEvents(new AppointmentNoted(id, "one"));

        await WhenPatchedToRoute(new NoteAppointment(id, "two"), "NoteAppointment");
        ThenEvents(new AppointmentNoted(id, "two"));
    }

    [Fact]
    public async Task delete_sends_the_command_as_its_body()
    {
        var id = await scheduled();

        await WhenDeleted(new WithdrawAppointment(id, "feeling better"));

        ThenResponseIs(204);
        ThenEvents(new AppointmentWithdrawn(id, "feeling better"));
    }

    [Fact]
    public async Task delete_by_route_and_by_route_name()
    {
        var first = await scheduled();
        await WhenDeleted(new WithdrawAppointment(first, "a"), $"/api/appointments/{first}");
        ThenEvents(new AppointmentWithdrawn(first, "a"));

        var second = await scheduled();
        await WhenDeletedToRoute(new WithdrawAppointment(second, "b"), "WithdrawAppointment");
        ThenEvents(new AppointmentWithdrawn(second, "b"));
    }

    [Fact]
    public async Task a_missing_aggregate_on_a_put_is_a_404_refusal()
    {
        await WhenPut(new RenamePatient(Guid.NewGuid(), "Bea"));

        ThenNotFound();
        ThenNoEvents();
        LastAct.Refusal.ShouldNotBeNull();
    }

    [Fact]
    public async Task post_by_route_name()
    {
        var id = await scheduled();

        await WhenPostedToRoute(new ConfirmAppointmentRequest(id), "ConfirmAppointment");

        ThenEvents(typeof(AppointmentConfirmed));
    }

    [Fact]
    public async Task a_route_name_on_another_verb_is_refused()
    {
        (await Should.ThrowAsync<InvalidOperationException>(async () =>
                await WhenPostedToRoute(new RenamePatient(Guid.NewGuid(), "x"), "RenamePatient")))
            .Message.ShouldContain("No endpoint answering POST has the route name 'RenamePatient'");
    }
}
