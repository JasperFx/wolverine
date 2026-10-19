using JasperFx.Events;
using Microsoft.AspNetCore.Mvc;
using Wolverine.Http;
using Wolverine.Persistence;
using Wolverine.Persistence.EventSourcing;

namespace Wolverine.Bobcat.Http.Tests;

public record AppointmentScheduled(Guid AppointmentId, string Patient);

public record AppointmentConfirmed(Guid AppointmentId);

public record AppointmentCancelled(Guid AppointmentId, string Reason);

public record SendConfirmationEmail(Guid AppointmentId);

public class Appointment
{
    public Guid Id { get; set; }
    public string Patient { get; set; } = "";
    public bool Confirmed { get; set; }
    public bool Cancelled { get; set; }

    public void Apply(AppointmentScheduled e)
    {
        Id = e.AppointmentId;
        Patient = e.Patient;
    }

    public void Apply(AppointmentConfirmed e) => Confirmed = true;
    public void Apply(AppointmentCancelled e) => Cancelled = true;
    public void Apply(PatientRenamed e) => Patient = e.Patient;
}

public record ConfirmAppointmentRequest(Guid AppointmentId);

public record ConfirmationResponse(Guid AppointmentId, string Status);

public static class ConfirmAppointmentEndpoint
{
    public static ProblemDetails Validate(ConfirmAppointmentRequest request, Appointment appointment)
        => appointment.Confirmed
            ? new ProblemDetails { Detail = "The appointment is already confirmed", Status = 400 }
            : WolverineContinue.NoProblems;

    [WolverinePost("/api/appointments/confirm", RouteName = "ConfirmAppointment")]
    public static (ConfirmationResponse, EventsToAppend, OutgoingMessages) Post(ConfirmAppointmentRequest request,
        [WriteModel] Appointment appointment)
        => (new ConfirmationResponse(request.AppointmentId, "Confirmed"),
            new EventsToAppend { new AppointmentConfirmed(request.AppointmentId) },
            new OutgoingMessages { new SendConfirmationEmail(request.AppointmentId) });
}

public record CancelAppointment(Guid AppointmentId, string Reason);

public static class CancelAppointmentEndpoint
{
    // a route parameter AND a body: WhenPosted fills {appointmentId} from the command
    [WolverinePost("/api/appointments/{appointmentId}/cancel")]
    [EmptyResponse]
    public static AppointmentCancelled Post(CancelAppointment command, [WriteModel] Appointment appointment)
        => new(command.AppointmentId, command.Reason);
}

public record AmbiguousRequest(string Name);

public static class AmbiguousEndpoints
{
    [WolverinePost("/api/ambiguous/one")]
    public static string One(AmbiguousRequest request) => "one";

    [WolverinePost("/api/ambiguous/two")]
    public static string Two(AmbiguousRequest request) => "two";
}

public record ExplodingRequest(string Name);

public static class ExplodingEndpoint
{
    [WolverinePost("/api/explode")]
    public static string Post(ExplodingRequest request) => throw new DivideByZeroException("boom");
}

public static class SendConfirmationEmailHandler
{
    public static void Handle(SendConfirmationEmail message)
    {
    }
}

// GH-4931: an endpoint that appends and cascades, and one that starts a stream with an id it assigns
public record RemindPatient(Guid AppointmentId);

public record PatientReminded(Guid AppointmentId);

public record LogReminder(Guid AppointmentId);

public record ReminderLogged(Guid AppointmentId);

public static class RemindPatientEndpoint
{
    [WolverinePost("/api/appointments/remind")]
    public static (string, EventsToAppend, OutgoingMessages) Post(RemindPatient command, [WriteModel] Appointment appointment)
        => ("Reminded",
            new EventsToAppend { new PatientReminded(command.AppointmentId) },
            new OutgoingMessages { new LogReminder(command.AppointmentId) });
}

public static class LogReminderHandler
{
    public static void Handle(LogReminder message, [WriteModel] IEventStream<Appointment> appointment)
        => appointment.AppendOne(new ReminderLogged(message.AppointmentId));
}

public record BookAppointment(string Patient);

public static class BookAppointmentEndpoint
{
    [WolverinePost("/api/appointments/book")]
    [EmptyResponse]
    public static StartStream Post(BookAppointment command)
    {
        var id = Guid.CreateVersion7();
        return Storage.StartStream<Appointment>(id, new AppointmentScheduled(id, command.Patient));
    }
}

// GH-4940: an HTTP endpoint deciding over a Dynamic Consistency Boundary; the tag query its Load() returns is
// recorded for the act as a message handler's is
public record HallId(Guid Value);

public record HallBooked(HallId Hall, string Organizer);

public record BookHall(HallId Hall, string Organizer);

[JasperFx.Events.Aggregation.BoundaryAggregate]
public partial class HallBookings
{
    public int Count { get; private set; }

    public void Apply(HallBooked e) => Count++;
}

public static class BookHallEndpoint
{
    public static JasperFx.Events.Tags.EventTagQuery Load(BookHall command)
        => JasperFx.Events.Tags.EventTagQuery.For(command.Hall).AndEventsOfType<HallBooked>();

    [WolverinePost("/api/halls/book")]
    [EmptyResponse]
    public static HallBooked Post(BookHall command, [DcbModel] HallBookings? bookings)
        => new(command.Hall, command.Organizer);
}

// GH-4939: an endpoint that uses only an event store session -- no IMessageBus, no IMessageContext -- so
// RequestIdMiddleware leaves it alone unless WolverineHttpOptions.ApplyCorrelationIdHeaderToAllEndpoints is on
public record AddPatientNote(string Patient, string Note);

public record PatientNoteAdded(string Patient, string Note);

public static class AddPatientNoteEndpoint
{
    [WolverinePost("/api/patients/notes")]
    [EmptyResponse]
    public static void Post(AddPatientNote command, global::Marten.IDocumentSession session)
        => session.Events.StartStream(Guid.CreateVersion7(), new PatientNoteAdded(command.Patient, command.Note));
}

// Read acts: a GET of an event-sourced aggregate (404 when the stream is missing), and a GET addressed by route name
public record AppointmentSummary(Guid Id, string Patient, bool Confirmed);

public static class AppointmentQueries
{
    [WolverineGet("/api/appointments/{id}")]
    public static Appointment Get([global::Wolverine.Marten.ReadAggregate] Appointment appointment) => appointment;

    [WolverineGet("/api/appointments/{id}/summary", RouteName = "AppointmentSummary")]
    public static AppointmentSummary Summary([global::Wolverine.Http.Marten.Document] Appointment appointment)
        => new(appointment.Id, appointment.Patient, appointment.Confirmed);
}

// PUT / PATCH / DELETE command acts, each with a route name
public record RenamePatient(Guid AppointmentId, string Patient);

public record PatientRenamed(Guid AppointmentId, string Patient);

public record NoteAppointment(Guid AppointmentId, string Note);

public record AppointmentNoted(Guid AppointmentId, string Note);

public record WithdrawAppointment(Guid AppointmentId, string Reason);

public record AppointmentWithdrawn(Guid AppointmentId, string Reason);

public static class AppointmentCommandEndpoints
{
    [WolverinePut("/api/appointments/{appointmentId}/patient", RouteName = "RenamePatient")]
    [EmptyResponse]
    public static PatientRenamed Put(RenamePatient command, [WriteModel] Appointment appointment)
        => new(command.AppointmentId, command.Patient);

    [WolverinePatch("/api/appointments/{appointmentId}/notes", RouteName = "NoteAppointment")]
    public static (AppointmentNoted, EventsToAppend) Patch(NoteAppointment command, [WriteModel] Appointment appointment)
        => (new AppointmentNoted(command.AppointmentId, command.Note),
            new EventsToAppend { new AppointmentNoted(command.AppointmentId, command.Note) });

    [WolverineDelete("/api/appointments/{appointmentId}", RouteName = "WithdrawAppointment")]
    [EmptyResponse]
    public static AppointmentWithdrawn Delete(WithdrawAppointment command, [WriteModel] Appointment appointment)
        => new(command.AppointmentId, command.Reason);
}

// GH-5076: body-less acts -- each endpoint takes everything from its route
public record PatientArrivedAtDesk(Guid AppointmentId);

public record AppointmentHeld(Guid AppointmentId);

public record AppointmentFlagged(Guid AppointmentId);

public record AppointmentUnconfirmed(Guid AppointmentId);

public static class BodyLessEndpoints
{
    [WolverinePost("/api/appointments/{id}/arrive")]
    [EmptyResponse]
    public static PatientArrivedAtDesk Arrive([WriteModel] Appointment appointment) => new(appointment.Id);

    [WolverinePut("/api/appointments/{id}/hold")]
    [EmptyResponse]
    public static AppointmentHeld Hold([WriteModel] Appointment appointment) => new(appointment.Id);

    [WolverinePatch("/api/appointments/{id}/flag")]
    [EmptyResponse]
    public static AppointmentFlagged Flag([WriteModel] Appointment appointment) => new(appointment.Id);

    [WolverineDelete("/api/appointments/{id}/confirmation")]
    [EmptyResponse]
    public static AppointmentUnconfirmed Unconfirm([WriteModel] Appointment appointment) => new(appointment.Id);
}

// GH-5076: a body-less endpoint that needs a header, for the configure overloads
public record EscalationResponse(Guid Id, string By);

public static class EscalateEndpoint
{
    [WolverinePost("/api/appointments/{id}/escalate")]
    public static Microsoft.AspNetCore.Http.IResult Post(Guid id, Microsoft.AspNetCore.Http.HttpRequest request)
        => request.Headers.TryGetValue("x-escalated-by", out var by)
            ? Microsoft.AspNetCore.Http.Results.Json(new EscalationResponse(id, by.ToString()))
            : Microsoft.AspNetCore.Http.Results.BadRequest();
}

// wolverine#5077: an endpoint that schedules a timeout -- what an act calling it caused, never the act's own message
public record ReminderDue(Guid AppointmentId) : TimeoutMessage(TimeSpan.FromDays(1));

public record ReminderSent(Guid AppointmentId);

public static class RemindLaterEndpoint
{
    [WolverinePost("/api/appointments/{id}/remind-later")]
    [EmptyResponse]
    public static OutgoingMessages Post(Guid id) => [new ReminderDue(id)];
}

public static class ReminderDueHandler
{
    public static ReminderSent Handle(ReminderDue message) => new(message.AppointmentId);
}

public static class ReminderSentHandler
{
    public static void Handle(ReminderSent message)
    {
    }
}
